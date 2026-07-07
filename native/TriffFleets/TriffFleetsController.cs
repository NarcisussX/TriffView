using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace TriffView.TriffFleets;

internal sealed class TriffFleetsController
{
    private const string ClientId = "7d2454c3191c4254a4b67d8f71f2b972";
    private const string RedirectUri = "http://127.0.0.1:51777/trifffleets/callback/";
    private const string AuthorizeEndpoint = "https://login.eveonline.com/v2/oauth/authorize";
    private const string TokenEndpoint = "https://login.eveonline.com/v2/oauth/token";
    private const string EsiBaseUrl = "https://esi.evetech.net/latest";
    private const string Scopes = "esi-fleets.read_fleet.v1 esi-fleets.write_fleet.v1";
    private const int WriteThrottleMs = 250;
    private const int MemberWriteConcurrency = 4;
    private const int MemberMoveSettleBeforePruneMs = 1200;
    private const int StructureSettleBeforeMemberReadMs = 1000;
    private const int CleanupDeleteConcurrency = 6;
    private const int EsiTransientMaxAttempts = 3;
    private const int EsiTransientBaseDelayMs = 650;
    private const int FleetStructureNameMaxLength = 10;
    private const string BenchWingName = "Bench";
    private const string BenchSquadName = "Waiting";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly Dispatcher _dispatcher;
    private readonly Action<object> _postToHud;
    private readonly TriffFleetsLocalState _state;
    private readonly Dictionary<long, AccessTokenCache> _accessTokens = new();
    private LiveFleetInfo? _liveFleet;
    private FleetDryRunPlan? _lastPlan;
    private FleetApplySummary? _lastApply;
    private bool _authInProgress;
    private string _lastPostedStateJson = "";

    public TriffFleetsController(Dispatcher dispatcher, Action<object> postToHud)
    {
        _dispatcher = dispatcher;
        _postToHud = postToHud;
        _state = TriffFleetsLocalState.Load();
    }

    public bool HandleWebMessage(string type, JsonObject? message)
    {
        switch (type)
        {
            case "trifffleets:get-state":
                PostState(force: true);
                return true;
            case "trifffleets:start-auth":
                _ = StartAuthAsync();
                return true;
            case "trifffleets:select-boss":
                SelectBoss(ReadLong(message, "characterId"));
                return true;
            case "trifffleets:forget-boss":
                ForgetBoss(ReadLong(message, "characterId"));
                return true;
            case "trifffleets:detect-fleet":
                _ = DetectFleetAsync();
                return true;
            case "trifffleets:create-profile":
                CreateProfile(message?["name"]?.GetValue<string>());
                return true;
            case "trifffleets:select-profile":
                SelectProfile(message?["profileId"]?.GetValue<string>());
                return true;
            case "trifffleets:save-profile":
                SaveProfile(message?["profile"] as JsonObject);
                return true;
            case "trifffleets:delete-profile":
                DeleteProfile(message?["profileId"]?.GetValue<string>());
                return true;
            case "trifffleets:import-profile-json":
                ImportProfileJson();
                return true;
            case "trifffleets:export-profile-json":
                ExportProfileJson(message?["profileId"]?.GetValue<string>());
                return true;
            case "trifffleets:refresh-character-cache":
                _ = BuildPlanAsync(refreshCharacters: true);
                return true;
            case "trifffleets:build-plan":
                _ = BuildPlanAsync(refreshCharacters: message?["refreshCharacters"]?.GetValue<bool>() == true);
                return true;
            case "trifffleets:apply-plan":
                _ = ApplyPlanAsync();
                return true;
            default:
                return false;
        }
    }

    private static long ReadLong(JsonObject? message, string key)
    {
        if (message?[key] == null) return 0;
        try
        {
            return message[key]!.GetValue<long>();
        }
        catch
        {
            return 0;
        }
    }

    private void SelectBoss(long characterId)
    {
        if (characterId <= 0 || _state.Bosses.All(boss => boss.CharacterId != characterId)) return;
        _state.SelectedBossCharacterId = characterId;
        _state.Save();
        _liveFleet = null;
        _lastPlan = null;
        _lastApply = null;
        PostState(force: true);
    }

    private void ForgetBoss(long characterId)
    {
        if (characterId <= 0) return;
        CredentialStore.Delete(RefreshTokenTarget(characterId));
        _state.Bosses.RemoveAll(boss => boss.CharacterId == characterId);
        if (_state.SelectedBossCharacterId == characterId)
        {
            _state.SelectedBossCharacterId = _state.Bosses.FirstOrDefault()?.CharacterId ?? 0;
        }
        _accessTokens.Remove(characterId);
        _liveFleet = null;
        _lastPlan = null;
        _lastApply = null;
        _state.Save();
        PostState(force: true);
    }

    private void CreateProfile(string? name)
    {
        var profile = FleetProfile.Default(string.IsNullOrWhiteSpace(name) ? "New Fleet Profile" : name.Trim());
        _state.Profiles.Add(profile);
        _state.SelectedProfileId = profile.Id;
        _state.Save();
        _lastPlan = null;
        _lastApply = null;
        PostState(force: true);
    }

    private void SelectProfile(string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId) || _state.Profiles.All(profile => profile.Id != profileId)) return;
        _state.SelectedProfileId = profileId;
        _state.Save();
        _lastPlan = null;
        _lastApply = null;
        PostState(force: true);
    }

    private void SaveProfile(JsonObject? profileJson)
    {
        if (profileJson == null)
        {
            PostError("profile", "No fleet profile was supplied.");
            return;
        }

        FleetProfile? profile;
        try
        {
            profile = profileJson.Deserialize<FleetProfile>(JsonOptions)?.Normalize();
        }
        catch (Exception ex)
        {
            PostError("profile", $"Profile JSON could not be read: {ex.Message}");
            return;
        }

        if (profile == null)
        {
            PostError("profile", "Profile JSON could not be read.");
            return;
        }

        var index = _state.Profiles.FindIndex(existing => existing.Id == profile.Id);
        if (index >= 0) _state.Profiles[index] = profile;
        else _state.Profiles.Add(profile);
        _state.SelectedProfileId = profile.Id;
        _state.Save();
        _lastPlan = null;
        _lastApply = null;
        PostState(force: true);
    }

    private void DeleteProfile(string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId)) return;
        if (_state.Profiles.Count <= 1)
        {
            PostError("profile", "Keep at least one fleet profile.");
            return;
        }

        _state.Profiles.RemoveAll(profile => profile.Id == profileId);
        if (_state.SelectedProfileId == profileId)
        {
            _state.SelectedProfileId = _state.Profiles.FirstOrDefault()?.Id ?? "";
        }

        _state.Save();
        _lastPlan = null;
        _lastApply = null;
        PostState(force: true);
    }

    private void ImportProfileJson()
    {
        _dispatcher.Invoke(() =>
        {
            using var dialog = new Forms.OpenFileDialog
            {
                Title = "Import TriffFleets profile JSON",
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false,
            };

            if (dialog.ShowDialog() != Forms.DialogResult.OK) return;

            try
            {
                var profile = JsonSerializer.Deserialize<FleetProfile>(File.ReadAllText(dialog.FileName), JsonOptions)?.Normalize();
                if (profile == null) throw new InvalidDataException("Profile JSON was empty.");
                profile.Id = Guid.NewGuid().ToString("N");
                if (string.IsNullOrWhiteSpace(profile.Name)) profile.Name = Path.GetFileNameWithoutExtension(dialog.FileName);
                _state.Profiles.Add(profile);
                _state.SelectedProfileId = profile.Id;
                _state.Save();
                _lastPlan = null;
                _lastApply = null;
                PostState(force: true);
            }
            catch (Exception ex)
            {
                PostError("import", $"Could not import profile JSON: {ex.Message}");
            }
        });
    }

    private void ExportProfileJson(string? profileId)
    {
        var profile = ProfileById(profileId) ?? SelectedProfile();
        if (profile == null)
        {
            PostError("export", "No fleet profile is selected.");
            return;
        }

        _dispatcher.Invoke(() =>
        {
            using var dialog = new Forms.SaveFileDialog
            {
                Title = "Export TriffFleets profile JSON",
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                FileName = $"{SafeFileName(profile.Name)}.json",
                OverwritePrompt = true,
            };

            if (dialog.ShowDialog() != Forms.DialogResult.OK) return;

            try
            {
                File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(profile, JsonOptions), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                PostError("export", $"Could not export profile JSON: {ex.Message}");
            }
        });
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string((value.Length == 0 ? "fleet-profile" : value).Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "fleet-profile" : cleaned;
    }

    private async Task StartAuthAsync()
    {
        if (_authInProgress)
        {
            PostError("auth", "Fleet boss authentication is already in progress.");
            return;
        }

        if (string.IsNullOrWhiteSpace(ClientId))
        {
            PostError("auth", "TriffFleets needs a registered EVE SSO client ID before fleet boss authentication can run. Register the TriffView app in the EVE developer portal and set the built-in client ID in TriffFleetsController.");
            PostState(force: true);
            return;
        }

        _authInProgress = true;
        PostState(force: true);

        using var listener = new TcpListener(IPAddress.Loopback, 51777);
        try
        {
            var state = Base64Url(RandomNumberGenerator.GetBytes(32));
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            listener.Start();

            var authUrl = BuildAuthorizeUrl(state, challenge);
            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

            var contextTask = listener.AcceptTcpClientAsync();
            var completed = await Task.WhenAny(contextTask, Task.Delay(TimeSpan.FromMinutes(5)));
            if (completed != contextTask)
            {
                PostError("auth", "EVE SSO authentication timed out.");
                return;
            }

            using var client = await contextTask;
            await using var stream = client.GetStream();
            var callbackUrl = await ReadCallbackUrlAsync(stream);
            var query = ParseQuery(callbackUrl.Query);
            var error = query.TryGetValue("error", out var errorValue) ? errorValue : "";
            var code = query.TryGetValue("code", out var codeValue) ? codeValue : "";
            var returnedState = query.TryGetValue("state", out var stateValue) ? stateValue : "";

            if (!string.IsNullOrWhiteSpace(error))
            {
                await WriteCallbackHtmlAsync(stream, "TriffFleets authentication was cancelled or denied. You can close this tab.");
                PostError("auth", $"EVE SSO returned: {error}");
                return;
            }

            if (!string.Equals(state, returnedState, StringComparison.Ordinal))
            {
                await WriteCallbackHtmlAsync(stream, "TriffFleets blocked this login because the SSO state did not match. You can close this tab.");
                PostError("auth", "EVE SSO state did not match. Authentication was blocked.");
                return;
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                await WriteCallbackHtmlAsync(stream, "TriffFleets did not receive an authorization code. You can close this tab.");
                PostError("auth", "EVE SSO did not return an authorization code.");
                return;
            }

            var token = await ExchangeCodeAsync(code, verifier);
            var identity = DecodeEveJwt(token.AccessToken);
            if (identity.CharacterId <= 0)
            {
                throw new InvalidDataException("The EVE SSO token did not include a character ID.");
            }

            if (!identity.Scopes.Contains("esi-fleets.read_fleet.v1") || !identity.Scopes.Contains("esi-fleets.write_fleet.v1"))
            {
                throw new InvalidDataException("The selected character did not grant the required fleet scopes.");
            }

            if (string.IsNullOrWhiteSpace(token.RefreshToken))
            {
                throw new InvalidDataException("EVE SSO did not return a refresh token.");
            }

            CredentialStore.Write(RefreshTokenTarget(identity.CharacterId), token.RefreshToken);
            _accessTokens[identity.CharacterId] = new AccessTokenCache(token.AccessToken, DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn - 60)));

            var existing = _state.Bosses.FirstOrDefault(boss => boss.CharacterId == identity.CharacterId);
            if (existing == null)
            {
                _state.Bosses.Add(new FleetBossAuth
                {
                    CharacterId = identity.CharacterId,
                    CharacterName = identity.CharacterName,
                    Scopes = identity.Scopes.ToList(),
                    AuthenticatedUtc = DateTimeOffset.UtcNow,
                });
            }
            else
            {
                existing.CharacterName = identity.CharacterName;
                existing.Scopes = identity.Scopes.ToList();
                existing.AuthenticatedUtc = DateTimeOffset.UtcNow;
            }

            _state.SelectedBossCharacterId = identity.CharacterId;
            _state.Save();
            _liveFleet = null;
            _lastPlan = null;
            _lastApply = null;
            await WriteCallbackHtmlAsync(stream, "TriffFleets authentication complete. You can close this tab and return to TriffView.");
            PostState(force: true);
        }
        catch (SocketException ex)
        {
            PostError("auth", $"Could not open the local SSO callback listener at {RedirectUri}. {ex.Message}");
        }
        catch (Exception ex)
        {
            PostError("auth", ex.Message);
        }
        finally
        {
            _authInProgress = false;
            listener.Stop();
            PostState(force: true);
        }
    }

    private static async Task<Uri> ReadCallbackUrlAsync(NetworkStream stream)
    {
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync() ?? "";
        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            throw new InvalidDataException("Local SSO callback was not a valid HTTP request.");
        }

        while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
        {
            // Drain headers before writing the callback page.
        }

        return new Uri(new Uri(RedirectUri), parts[1]);
    }

    private static async Task WriteCallbackHtmlAsync(NetworkStream stream, string message)
    {
        var html = $"""
        <!doctype html>
        <html>
        <head><meta charset="utf-8"><title>TriffFleets</title></head>
        <body style="margin:0;background:#05070b;color:#d9e2ee;font-family:Segoe UI,Arial,sans-serif;">
          <main style="max-width:520px;margin:80px auto;border:1px solid #303640;background:#090d14;padding:24px;">
            <h1 style="color:#53b6ff;font-size:18px;text-transform:uppercase;">TriffFleets</h1>
            <p>{WebUtility.HtmlEncode(message)}</p>
          </main>
        </body>
        </html>
        """;
        var body = Encoding.UTF8.GetBytes(html);
        var header = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n"
        );
        await stream.WriteAsync(header);
        await stream.WriteAsync(body);
    }

    private static string BuildAuthorizeUrl(string state, string challenge)
    {
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = ClientId,
            ["scope"] = Scopes,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };
        return $"{AuthorizeEndpoint}?{BuildQuery(query)}";
    }

    private static string BuildQuery(Dictionary<string, string> values)
    {
        return string.Join("&", values.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        query = query.TrimStart('?');
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            var key = Uri.UnescapeDataString(pieces[0].Replace("+", " "));
            var value = pieces.Length > 1 ? Uri.UnescapeDataString(pieces[1].Replace("+", " ")) : "";
            result[key] = value;
        }
        return result;
    }

    private async Task<TokenResponse> ExchangeCodeAsync(string code, string verifier)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = ClientId,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = RedirectUri,
        };
        return await SendTokenRequestAsync(form);
    }

    private async Task<TokenResponse> RefreshTokenAsync(long characterId)
    {
        var refreshToken = CredentialStore.Read(RefreshTokenTarget(characterId));
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException("This fleet boss needs to authenticate again.");
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = ClientId,
        };
        var token = await SendTokenRequestAsync(form);
        if (!string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            CredentialStore.Write(RefreshTokenTarget(characterId), token.RefreshToken);
        }
        _accessTokens[characterId] = new AccessTokenCache(token.AccessToken, DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn - 60)));
        return token;
    }

    private static async Task<TokenResponse> SendTokenRequestAsync(Dictionary<string, string> form)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.UserAgent.ParseAdd("TriffView/1.0 TriffFleets");

        using var response = await Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"EVE SSO returned {(int)response.StatusCode}: {ReadError(text)}");
        }

        return JsonSerializer.Deserialize<TokenResponse>(text, JsonOptions)
            ?? throw new InvalidDataException("EVE SSO returned an empty token response.");
    }

    private async Task<string> AccessTokenForSelectedBossAsync()
    {
        var boss = SelectedBoss() ?? throw new InvalidOperationException("Authenticate or select a fleet boss first.");
        if (_accessTokens.TryGetValue(boss.CharacterId, out var cached) && cached.ExpiresUtc > DateTimeOffset.UtcNow.AddSeconds(30))
        {
            return cached.AccessToken;
        }

        var token = await RefreshTokenAsync(boss.CharacterId);
        return token.AccessToken;
    }

    private async Task DetectFleetAsync()
    {
        try
        {
            var boss = SelectedBoss() ?? throw new InvalidOperationException("Authenticate or select a fleet boss first.");
            var token = await AccessTokenForSelectedBossAsync();
            var response = await SendEsiAsync<CharacterFleetInfo>(HttpMethod.Get, $"/characters/{boss.CharacterId}/fleet/", token);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _liveFleet = null;
                _lastPlan = null;
                _lastApply = null;
                PostError("detect", "This character is not in a fleet yet. Create a fleet in-game on this character first, then click Detect Fleet again. ESI can take up to 60 seconds to show a new fleet.");
                PostState(force: true);
                return;
            }

            response.ThrowIfFailed();
            var info = response.Value ?? throw new InvalidDataException("ESI returned an empty fleet response.");
            var canModify = info.FleetBossId == boss.CharacterId || string.Equals(info.Role, "fleet_commander", StringComparison.OrdinalIgnoreCase);

            _liveFleet = new LiveFleetInfo
            {
                FleetId = info.FleetId,
                FleetBossId = info.FleetBossId,
                Role = info.Role,
                WingId = info.WingId,
                SquadId = info.SquadId,
                CanModify = canModify,
                DetectedUtc = DateTimeOffset.UtcNow,
            };
            _lastPlan = null;
            _lastApply = null;

            if (!canModify)
            {
                PostError("detect", "Fleet detected, but this character does not appear to be the fleet boss or fleet commander. Select the fleet boss character or adjust fleet leadership in-game.");
            }
            PostState(force: true);
        }
        catch (Exception ex)
        {
            PostError("detect", ex.Message);
            PostState(force: true);
        }
    }

    private async Task BuildPlanAsync(bool refreshCharacters)
    {
        try
        {
            var profile = SelectedProfile() ?? throw new InvalidOperationException("Create or select a fleet profile first.");
            var liveFleet = RequireLiveFleet();
            var token = await AccessTokenForSelectedBossAsync();
            var plan = await BuildPlanCoreAsync(profile, liveFleet, token, refreshCharacters);
            _lastPlan = plan;
            _lastApply = null;
            PostState(force: true);
        }
        catch (Exception ex)
        {
            PostError("dry-run", ex.Message);
            PostState(force: true);
        }
    }

    private async Task<FleetDryRunPlan> BuildPlanCoreAsync(FleetProfile profile, LiveFleetInfo liveFleet, string token, bool refreshCharacters)
    {
        var wingsResponse = await SendEsiAsync<List<EsiWing>>(HttpMethod.Get, $"/fleets/{liveFleet.FleetId}/wings/", token);
        wingsResponse.ThrowIfFailed();
        var membersResponse = await SendEsiAsync<List<EsiFleetMember>>(HttpMethod.Get, $"/fleets/{liveFleet.FleetId}/members/", token);
        membersResponse.ThrowIfFailed();

        var liveWings = wingsResponse.Value ?? new List<EsiWing>();
        var liveMembers = membersResponse.Value ?? new List<EsiFleetMember>();
        var memberIds = liveMembers.Select(member => member.CharacterId).ToHashSet();
        var validation = ValidateProfile(profile);
        var namesToResolve = profile.AllMembers()
            .Select(member => member.CharacterName.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var resolved = await ResolveCharacterIdsAsync(namesToResolve, refreshCharacters);

        var plan = new FleetDryRunPlan
        {
            GeneratedUtc = DateTimeOffset.UtcNow,
            FleetId = liveFleet.FleetId,
            ProfileId = profile.Id,
            ProfileName = profile.Name,
            CanApply = validation.Errors.Count == 0 && liveFleet.CanModify,
            Warnings = validation.Warnings,
            Errors = validation.Errors,
        };

        if (!liveFleet.CanModify)
        {
            plan.Errors.Add("Selected fleet boss does not appear to have permission to modify this fleet.");
            plan.CanApply = false;
        }

        foreach (var profileWing in profile.Wings.Select((wing, index) => new { wing, index }))
        {
            var desiredWingName = NormalizeFleetStructureName(profileWing.wing.Name);
            var liveWing = profileWing.index < liveWings.Count ? liveWings[profileWing.index] : null;
            if (liveWing == null)
            {
                plan.WingsToCreate.Add(new FleetStructureAction(profileWing.index + 1, desiredWingName, null));
            }
            else if (!string.Equals(liveWing.Name, desiredWingName, StringComparison.Ordinal))
            {
                plan.WingsToRename.Add(new FleetRenameAction(liveWing.Id, liveWing.Name, desiredWingName));
            }

            var liveSquads = liveWing?.Squads ?? new List<EsiSquad>();
            foreach (var profileSquad in profileWing.wing.Squads.Select((squad, index) => new { squad, index }))
            {
                var desiredSquadName = NormalizeFleetStructureName(profileSquad.squad.Name);
                var liveSquad = profileSquad.index < liveSquads.Count ? liveSquads[profileSquad.index] : null;
                if (liveSquad == null)
                {
                    plan.SquadsToCreate.Add(new FleetStructureAction(profileSquad.index + 1, desiredSquadName, desiredWingName));
                }
                else if (!string.Equals(liveSquad.Name, desiredSquadName, StringComparison.Ordinal))
                {
                    plan.SquadsToRename.Add(new FleetRenameAction(liveSquad.Id, liveSquad.Name, desiredSquadName));
                }
            }
        }

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in profile.MemberPlacements())
        {
            var name = item.Member.CharacterName.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;

            if (!seenNames.Add(name))
            {
                plan.DuplicateCharacters.Add(name);
                continue;
            }

            if (!resolved.TryGetValue(name, out var characterId))
            {
                plan.UnresolvedCharacters.Add(name);
                continue;
            }

            var role = NormalizeRole(item.Member.Role);
            if (memberIds.Contains(characterId))
            {
                plan.AlreadyInFleet.Add(new FleetInvitePlan(name, characterId, role, item.WingName, item.SquadName));
            }
            else
            {
                plan.Invites.Add(new FleetInvitePlan(name, characterId, role, item.WingName, item.SquadName));
            }
        }

        if (plan.DuplicateCharacters.Count > 0)
        {
            plan.Errors.Add("Fleet profile has duplicate character names. Remove duplicates before applying.");
            plan.CanApply = false;
        }

        if (plan.UnresolvedCharacters.Count > 0)
        {
            plan.Errors.Add("Some character names could not be resolved by ESI.");
            plan.CanApply = false;
        }

        if (plan.Errors.Count > 0) plan.CanApply = false;
        return plan;
    }

    private async Task ApplyPlanAsync()
    {
        var results = new List<FleetApplyResult>();
        try
        {
            var profile = SelectedProfile() ?? throw new InvalidOperationException("Create or select a fleet profile first.");
            var liveFleet = RequireLiveFleet();
            if (!liveFleet.CanModify)
            {
                throw new InvalidOperationException("Selected fleet boss does not appear to have permission to modify this fleet.");
            }

            var token = await AccessTokenForSelectedBossAsync();
            var dryRun = await BuildPlanCoreAsync(profile, liveFleet, token, refreshCharacters: false);
            _lastPlan = dryRun;
            if (!dryRun.CanApply)
            {
                foreach (var error in dryRun.Errors)
                {
                    results.Add(new FleetApplyResult("preflight", profile.Name, 0, "failed validation", error));
                }

                foreach (var duplicate in dryRun.DuplicateCharacters)
                {
                    results.Add(FleetApplyResult.Member(duplicate, 0, "skipped duplicate", "Duplicate in saved profile."));
                }

                foreach (var unresolved in dryRun.UnresolvedCharacters)
                {
                    results.Add(FleetApplyResult.Member(unresolved, 0, "failed unresolved", "Character name did not resolve through ESI."));
                }

                if (results.Count == 0)
                {
                    results.Add(new FleetApplyResult("preflight", profile.Name, 0, "failed validation", "Fleet profile did not pass preflight validation."));
                }

                _lastApply = new FleetApplySummary
                {
                    AppliedUtc = DateTimeOffset.UtcNow,
                    Results = results,
                };
                PostError("apply", "Fleet profile did not pass preflight validation. Check the result log.");
                PostState(force: true);
                return;
            }

            var structure = await EnsureStructureAsync(profile, liveFleet, token, results);
            if (structure.Mutated)
            {
                await Task.Delay(StructureSettleBeforeMemberReadMs);
            }
            var membersResponse = await SendEsiAsync<List<EsiFleetMember>>(HttpMethod.Get, $"/fleets/{liveFleet.FleetId}/members/", token);
            membersResponse.ThrowIfFailed();
            var liveMembers = membersResponse.Value ?? new List<EsiFleetMember>();
            var memberById = liveMembers.ToDictionary(member => member.CharacterId);
            var names = profile.AllMembers().Select(member => member.CharacterName.Trim()).Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var resolved = await ResolveCharacterIdsAsync(names, refresh: false);
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var expectedCharacterIds = new HashSet<long>();
            var memberWrites = new List<Func<Task<bool>>>();
            var memberResultLock = new object();
            var memberPositionsChanged = false;

            foreach (var placement in profile.MemberPlacements())
            {
                var name = placement.Member.CharacterName.Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;

                if (!seenNames.Add(name))
                {
                    results.Add(FleetApplyResult.Member(name, 0, "skipped duplicate", "Duplicate in saved profile."));
                    continue;
                }

                if (!resolved.TryGetValue(name, out var characterId))
                {
                    results.Add(FleetApplyResult.Member(name, 0, "failed unresolved", "Character name did not resolve through ESI."));
                    continue;
                }

                var key = StructureKey(placement.WingIndex, placement.SquadIndex);
                if (!structure.Targets.TryGetValue(key, out var target))
                {
                    results.Add(FleetApplyResult.Member(name, characterId, "failed ESI error", $"Could not resolve live wing/squad for {placement.WingName} / {placement.SquadName}."));
                    continue;
                }

                var role = NormalizeRole(placement.Member.Role);
                expectedCharacterIds.Add(characterId);
                if (memberById.TryGetValue(characterId, out var liveMember))
                {
                    var capturedMember = liveMember;
                    var capturedName = name;
                    var capturedRole = role;
                    var capturedTarget = target;
                    memberWrites.Add(() => MoveExistingMemberIfNeededAsync(liveFleet.FleetId, capturedMember, capturedName, capturedRole, capturedTarget, token, results, memberResultLock));
                    continue;
                }

                var invitedName = name;
                var invitedCharacterId = characterId;
                var invitedRole = role;
                var invitedTarget = target;
                var invitedPlacement = placement;
                memberWrites.Add(async () =>
                {
                    var invite = CreateInvitation(invitedCharacterId, invitedRole, invitedTarget.WingId, invitedTarget.SquadId);
                    var inviteResponse = await SendEsiAsync<object>(HttpMethod.Post, $"/fleets/{liveFleet.FleetId}/members/", token, invite);
                    if (inviteResponse.IsSuccess)
                    {
                        AddResult(results, FleetApplyResult.Member(invitedName, invitedCharacterId, "invited", $"{invitedPlacement.WingName} / {invitedPlacement.SquadName}"), memberResultLock);
                    }
                    else
                    {
                        var status = inviteResponse.StatusCode == HttpStatusCode.UnprocessableEntity && inviteResponse.Error.Contains("CSPA", StringComparison.OrdinalIgnoreCase)
                            ? "failed CSPA/blocked"
                            : "failed ESI error";
                        AddResult(results, FleetApplyResult.Member(invitedName, invitedCharacterId, status, inviteResponse.Error), memberResultLock);
                    }

                    await Task.Delay(WriteThrottleMs);
                    return false;
                });
            }

            if (memberWrites.Count > 0)
            {
                var memberWriteResults = await RunMemberWriteBatchAsync(memberWrites);
                memberPositionsChanged |= memberWriteResults.Any(moved => moved);
            }

            var protectedWingIds = structure.Targets.Values.Select(target => target.WingId).ToHashSet();
            var protectedSquadIds = structure.Targets.Values.Select(target => target.SquadId).Where(id => id > 0).ToHashSet();
            if (profile.KeepExistingMembersInFleet)
            {
                var unexpectedMembers = liveMembers
                    .Where(member => !expectedCharacterIds.Contains(member.CharacterId))
                    .Where(member => member.CharacterId != liveFleet.FleetBossId)
                    .Where(member => NormalizeRole(member.Role) != "fleet_commander")
                    .ToList();
                if (unexpectedMembers.Count > 0)
                {
                    var benchTarget = await EnsureBenchAsync(liveFleet, token, structure.LiveWings, results);
                    protectedWingIds.Add(benchTarget.WingId);
                    protectedSquadIds.Add(benchTarget.SquadId);
                    memberPositionsChanged |= await MoveUnexpectedMembersToBenchAsync(liveFleet, token, unexpectedMembers, expectedCharacterIds, benchTarget, results);
                }
            }

            if (memberPositionsChanged)
            {
                await Task.Delay(MemberMoveSettleBeforePruneMs);
            }

            await PruneExtraStructureAsync(liveFleet, token, structure.LiveWings, liveMembers, protectedWingIds, protectedSquadIds, results);

            _lastApply = new FleetApplySummary
            {
                AppliedUtc = DateTimeOffset.UtcNow,
                Results = results,
            };
            PostState(force: true);
        }
        catch (Exception ex)
        {
            results.Add(new FleetApplyResult("system", "", 0, "failed ESI error", ex.Message));
            _lastApply = new FleetApplySummary
            {
                AppliedUtc = DateTimeOffset.UtcNow,
                Results = results,
            };
            PostError("apply", ex.Message);
            PostState(force: true);
        }
    }

    private async Task<FleetStructureResult> EnsureStructureAsync(FleetProfile profile, LiveFleetInfo liveFleet, string token, List<FleetApplyResult> results)
    {
        var wingsResponse = await SendEsiAsync<List<EsiWing>>(HttpMethod.Get, $"/fleets/{liveFleet.FleetId}/wings/", token);
        wingsResponse.ThrowIfFailed();
        var liveWings = wingsResponse.Value ?? new List<EsiWing>();
        var liveLayout = LiveLayoutFor(liveFleet.FleetId, profile.Id);
        var liveWingById = liveWings.Where(wing => wing.Id > 0).GroupBy(wing => wing.Id).ToDictionary(group => group.Key, group => group.First());
        var usedWingIds = new HashSet<long>();
        var map = new Dictionary<string, LivePlacementTarget>();
        var mutated = false;
        var liveLayoutChanged = false;

        foreach (var wingPlacement in profile.Wings.Select((wing, index) => new { wing, index }))
        {
            var desiredWingName = NormalizeFleetStructureName(wingPlacement.wing.Name);
            EsiWing? liveWing = null;
            var createdWing = false;
            var wingKey = WingStructureKey(wingPlacement.index);
            if (liveLayout.WingIds.TryGetValue(wingKey, out var mappedWingId) &&
                liveWingById.TryGetValue(mappedWingId, out var mappedWing))
            {
                liveWing = mappedWing;
            }

            liveWing ??= liveWings.FirstOrDefault(wing =>
                !usedWingIds.Contains(wing.Id) &&
                string.Equals(wing.Name, desiredWingName, StringComparison.Ordinal)
            );

            if (liveWing == null && wingPlacement.index < liveWings.Count && !usedWingIds.Contains(liveWings[wingPlacement.index].Id))
            {
                liveWing = liveWings[wingPlacement.index];
            }

            liveWing ??= liveWings.FirstOrDefault(wing => !usedWingIds.Contains(wing.Id));

            if (liveWing != null)
            {
                liveWing.Squads ??= new List<EsiSquad>();
            }
            else
            {
                var create = await SendEsiAsync<CreateWingResponse>(HttpMethod.Post, $"/fleets/{liveFleet.FleetId}/wings/", token);
                create.ThrowIfFailed();
                liveWing = new EsiWing { Id = create.Value?.WingId ?? 0, Name = "", Squads = new List<EsiSquad>() };
                liveWings.Add(liveWing);
                results.Add(new FleetApplyResult("wing", desiredWingName, liveWing.Id, "created", "Wing created."));
                createdWing = true;
                mutated = true;
                await Task.Delay(WriteThrottleMs);
            }

            usedWingIds.Add(liveWing.Id);
            liveLayoutChanged |= SetLiveLayoutId(liveLayout.WingIds, wingKey, liveWing.Id);

            if (createdWing || !string.Equals(liveWing.Name, desiredWingName, StringComparison.Ordinal))
            {
                var rename = await SendEsiAsync<object>(HttpMethod.Put, $"/fleets/{liveFleet.FleetId}/wings/{liveWing.Id}/", token, new { name = desiredWingName });
                rename.ThrowIfFailed();
                results.Add(new FleetApplyResult("wing", desiredWingName, liveWing.Id, "renamed", createdWing ? "Named after creation." : $"Renamed from {liveWing.Name}."));
                liveWing.Name = desiredWingName;
                mutated = true;
                await Task.Delay(WriteThrottleMs);
            }

            var liveSquadById = liveWing.Squads.Where(squad => squad.Id > 0).GroupBy(squad => squad.Id).ToDictionary(group => group.Key, group => group.First());
            var usedSquadIds = new HashSet<long>();
            foreach (var squadPlacement in wingPlacement.wing.Squads.Select((squad, index) => new { squad, index }))
            {
                var desiredSquadName = NormalizeFleetStructureName(squadPlacement.squad.Name);
                EsiSquad? liveSquad = null;
                var createdSquad = false;
                var squadKey = StructureKey(wingPlacement.index, squadPlacement.index);
                if (liveLayout.SquadIds.TryGetValue(squadKey, out var mappedSquadId) &&
                    liveSquadById.TryGetValue(mappedSquadId, out var mappedSquad))
                {
                    liveSquad = mappedSquad;
                }

                liveSquad ??= liveWing.Squads.FirstOrDefault(squad =>
                    !usedSquadIds.Contains(squad.Id) &&
                    string.Equals(squad.Name, desiredSquadName, StringComparison.Ordinal)
                );

                if (liveSquad == null && squadPlacement.index < liveWing.Squads.Count && !usedSquadIds.Contains(liveWing.Squads[squadPlacement.index].Id))
                {
                    liveSquad = liveWing.Squads[squadPlacement.index];
                }

                liveSquad ??= liveWing.Squads.FirstOrDefault(squad => !usedSquadIds.Contains(squad.Id));

                if (liveSquad == null)
                {
                    var create = await SendEsiAsync<CreateSquadResponse>(HttpMethod.Post, $"/fleets/{liveFleet.FleetId}/wings/{liveWing.Id}/squads/", token);
                    create.ThrowIfFailed();
                    liveSquad = new EsiSquad { Id = create.Value?.SquadId ?? 0, Name = "" };
                    liveWing.Squads.Add(liveSquad);
                    liveSquadById[liveSquad.Id] = liveSquad;
                    results.Add(new FleetApplyResult("squad", desiredSquadName, liveSquad.Id, "created", $"Under {desiredWingName}."));
                    createdSquad = true;
                    mutated = true;
                    await Task.Delay(WriteThrottleMs);
                }

                usedSquadIds.Add(liveSquad.Id);
                liveLayoutChanged |= SetLiveLayoutId(liveLayout.SquadIds, squadKey, liveSquad.Id);

                if (createdSquad || !string.Equals(liveSquad.Name, desiredSquadName, StringComparison.Ordinal))
                {
                    var rename = await SendEsiAsync<object>(HttpMethod.Put, $"/fleets/{liveFleet.FleetId}/squads/{liveSquad.Id}/", token, new { name = desiredSquadName });
                    rename.ThrowIfFailed();
                    results.Add(new FleetApplyResult("squad", desiredSquadName, liveSquad.Id, "renamed", createdSquad ? "Named after creation." : $"Renamed from {liveSquad.Name}."));
                    liveSquad.Name = desiredSquadName;
                    mutated = true;
                    await Task.Delay(WriteThrottleMs);
                }

                map[squadKey] = new LivePlacementTarget(liveWing.Id, liveSquad.Id);
            }
        }

        liveLayoutChanged |= PruneLiveLayout(profile, liveLayout);
        if (liveLayoutChanged)
        {
            liveLayout.UpdatedUtc = DateTimeOffset.UtcNow;
            _state.Save();
        }

        return new FleetStructureResult(map, liveWings, mutated);
    }

    private async Task<bool> MoveExistingMemberIfNeededAsync(long fleetId, EsiFleetMember member, string name, string role, LivePlacementTarget target, string token, List<FleetApplyResult> results, object? resultLock = null)
    {
        if (MemberMatchesTarget(member, role, target))
        {
            AddResult(results, FleetApplyResult.Member(name, member.CharacterId, "already in fleet", "Already in the requested fleet position."), resultLock);
            return false;
        }

        var move = CreateMovement(role, target.WingId, target.SquadId);
        var response = await SendEsiAsync<object>(HttpMethod.Put, $"/fleets/{fleetId}/members/{member.CharacterId}/", token, move);
        var moved = false;
        if (response.IsSuccess)
        {
            AddResult(results, FleetApplyResult.Member(name, member.CharacterId, "moved", "Moved to the saved wing/squad/role."), resultLock);
            member.Role = role;
            member.WingId = role == "fleet_commander" ? -1 : target.WingId;
            member.SquadId = role is "squad_commander" or "squad_member" ? target.SquadId : -1;
            moved = true;
        }
        else
        {
            AddResult(results, FleetApplyResult.Member(name, member.CharacterId, "failed ESI error", response.Error), resultLock);
        }

        await Task.Delay(WriteThrottleMs);
        return moved;
    }

    private async Task<LivePlacementTarget> EnsureBenchAsync(LiveFleetInfo liveFleet, string token, List<EsiWing> liveWings, List<FleetApplyResult> results)
    {
        var benchWing = liveWings.FirstOrDefault(wing => string.Equals(wing.Name, BenchWingName, StringComparison.OrdinalIgnoreCase));
        if (benchWing == null)
        {
            var create = await SendEsiAsync<CreateWingResponse>(HttpMethod.Post, $"/fleets/{liveFleet.FleetId}/wings/", token);
            create.ThrowIfFailed();
            benchWing = new EsiWing { Id = create.Value?.WingId ?? 0, Name = BenchWingName, Squads = new List<EsiSquad>() };
            liveWings.Add(benchWing);
            results.Add(new FleetApplyResult("wing", BenchWingName, benchWing.Id, "created", "Bench wing created for unassigned existing members."));
            await Task.Delay(WriteThrottleMs);

            var rename = await SendEsiAsync<object>(HttpMethod.Put, $"/fleets/{liveFleet.FleetId}/wings/{benchWing.Id}/", token, new { name = BenchWingName });
            rename.ThrowIfFailed();
            await Task.Delay(WriteThrottleMs);
        }

        var waitingSquad = benchWing.Squads.FirstOrDefault(squad => string.Equals(squad.Name, BenchSquadName, StringComparison.OrdinalIgnoreCase));
        if (waitingSquad == null)
        {
            var create = await SendEsiAsync<CreateSquadResponse>(HttpMethod.Post, $"/fleets/{liveFleet.FleetId}/wings/{benchWing.Id}/squads/", token);
            create.ThrowIfFailed();
            waitingSquad = new EsiSquad { Id = create.Value?.SquadId ?? 0, Name = BenchSquadName };
            benchWing.Squads.Add(waitingSquad);
            results.Add(new FleetApplyResult("squad", BenchSquadName, waitingSquad.Id, "created", "Bench waiting squad created for unassigned existing members."));
            await Task.Delay(WriteThrottleMs);

            var rename = await SendEsiAsync<object>(HttpMethod.Put, $"/fleets/{liveFleet.FleetId}/squads/{waitingSquad.Id}/", token, new { name = BenchSquadName });
            rename.ThrowIfFailed();
            await Task.Delay(WriteThrottleMs);
        }

        return new LivePlacementTarget(benchWing.Id, waitingSquad.Id);
    }

    private async Task<bool> MoveUnexpectedMembersToBenchAsync(LiveFleetInfo liveFleet, string token, List<EsiFleetMember> liveMembers, HashSet<long> expectedCharacterIds, LivePlacementTarget benchTarget, List<FleetApplyResult> results)
    {
        var memberWrites = new List<Func<Task<bool>>>();
        var resultLock = new object();
        foreach (var member in liveMembers)
        {
            if (expectedCharacterIds.Contains(member.CharacterId)) continue;

            var role = NormalizeRole(member.Role);
            if (role == "fleet_commander" || member.CharacterId == liveFleet.FleetBossId)
            {
                AddResult(results, FleetApplyResult.Member($"Character {member.CharacterId}", member.CharacterId, "kept command", "Unassigned fleet boss/fleet commander was not moved to Bench."), resultLock);
                continue;
            }

            if (role == "squad_member" && member.WingId == benchTarget.WingId && member.SquadId == benchTarget.SquadId)
            {
                continue;
            }

            var capturedMember = member;
            memberWrites.Add(async () =>
            {
                var move = CreateMovement("squad_member", benchTarget.WingId, benchTarget.SquadId);
                var response = await SendEsiAsync<object>(HttpMethod.Put, $"/fleets/{liveFleet.FleetId}/members/{capturedMember.CharacterId}/", token, move);
                var moved = false;
                if (response.IsSuccess)
                {
                    AddResult(results, FleetApplyResult.Member($"Character {capturedMember.CharacterId}", capturedMember.CharacterId, "benched", $"{BenchWingName} / {BenchSquadName}"), resultLock);
                    capturedMember.Role = "squad_member";
                    capturedMember.WingId = benchTarget.WingId;
                    capturedMember.SquadId = benchTarget.SquadId;
                    moved = true;
                }
                else
                {
                    AddResult(results, FleetApplyResult.Member($"Character {capturedMember.CharacterId}", capturedMember.CharacterId, "failed ESI error", response.Error), resultLock);
                }

                await Task.Delay(WriteThrottleMs);
                return moved;
            });
        }

        if (memberWrites.Count == 0) return false;
        var moveResults = await RunMemberWriteBatchAsync(memberWrites);
        return moveResults.Any(moved => moved);
    }

    private async Task PruneExtraStructureAsync(LiveFleetInfo liveFleet, string token, List<EsiWing> liveWings, List<EsiFleetMember> liveMembers, HashSet<long> protectedWingIds, HashSet<long> protectedSquadIds, List<FleetApplyResult> results)
    {
        var occupiedWingIds = liveMembers.Where(member => member.WingId > 0).Select(member => member.WingId).ToHashSet();
        var occupiedSquadIds = liveMembers.Where(member => member.SquadId > 0).Select(member => member.SquadId).ToHashSet();
        var resultLock = new object();
        var deletedSquadIds = new HashSet<long>();
        var deletedWingIds = new HashSet<long>();

        var squadDeleteCandidates = liveWings
            .SelectMany(wing => wing.Squads.Select(squad => new { Wing = wing, Squad = squad }))
            .Where(item => !protectedSquadIds.Contains(item.Squad.Id))
            .ToList();

        var squadsToDelete = new List<EsiSquad>();
        foreach (var item in squadDeleteCandidates)
        {
            if (occupiedSquadIds.Contains(item.Squad.Id))
            {
                results.Add(new FleetApplyResult("squad", item.Squad.Name, item.Squad.Id, "kept occupied", "Extra squad was not deleted because it has fleet members."));
            }
            else
            {
                squadsToDelete.Add(item.Squad);
            }
        }

        await RunCleanupBatchAsync(squadsToDelete, async squad =>
        {
            if (await DeleteSquadAsync(liveFleet.FleetId, squad, token, results, resultLock))
            {
                lock (deletedSquadIds)
                {
                    deletedSquadIds.Add(squad.Id);
                }
            }
        });

        if (deletedSquadIds.Count > 0)
        {
            foreach (var wing in liveWings)
            {
                wing.Squads.RemoveAll(squad => deletedSquadIds.Contains(squad.Id));
            }
        }

        var wingsToDelete = new List<EsiWing>();
        foreach (var wing in liveWings)
        {
            if (protectedWingIds.Contains(wing.Id) || wing.Squads.Any(squad => protectedSquadIds.Contains(squad.Id))) continue;

            var wingOccupied = occupiedWingIds.Contains(wing.Id) || wing.Squads.Any(squad => occupiedSquadIds.Contains(squad.Id));
            if (wingOccupied)
            {
                results.Add(new FleetApplyResult("wing", wing.Name, wing.Id, "kept occupied", "Extra wing was not deleted because it has fleet members."));
                continue;
            }

            if (wing.Squads.Count > 0) continue;
            wingsToDelete.Add(wing);
        }

        await RunCleanupBatchAsync(wingsToDelete, async wing =>
        {
            var deleteWing = await SendEsiAsync<object>(HttpMethod.Delete, $"/fleets/{liveFleet.FleetId}/wings/{wing.Id}/", token);
            if (deleteWing.IsSuccess)
            {
                lock (resultLock)
                {
                    results.Add(new FleetApplyResult("wing", wing.Name, wing.Id, "deleted", "Extra empty wing deleted."));
                }
                lock (deletedWingIds)
                {
                    deletedWingIds.Add(wing.Id);
                }
            }
            else
            {
                lock (resultLock)
                {
                    results.Add(new FleetApplyResult("wing", wing.Name, wing.Id, "failed ESI error", deleteWing.Error));
                }
            }
        });

        if (deletedWingIds.Count > 0)
        {
            liveWings.RemoveAll(wing => deletedWingIds.Contains(wing.Id));
        }
    }

    private static async Task<bool[]> RunMemberWriteBatchAsync(IEnumerable<Func<Task<bool>>> operations)
    {
        using var gate = new SemaphoreSlim(MemberWriteConcurrency);
        var tasks = operations.Select(async operation =>
        {
            await gate.WaitAsync();
            try
            {
                return await operation();
            }
            finally
            {
                gate.Release();
            }
        });
        return await Task.WhenAll(tasks);
    }

    private static async Task RunCleanupBatchAsync<T>(IEnumerable<T> items, Func<T, Task> worker)
    {
        using var gate = new SemaphoreSlim(CleanupDeleteConcurrency);
        var tasks = items.Select(async item =>
        {
            await gate.WaitAsync();
            try
            {
                await worker(item);
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);
    }

    private async Task<bool> DeleteSquadAsync(long fleetId, EsiSquad squad, string token, List<FleetApplyResult> results, object? resultLock = null)
    {
        var deleteSquad = await SendEsiAsync<object>(HttpMethod.Delete, $"/fleets/{fleetId}/squads/{squad.Id}/", token);
        if (deleteSquad.IsSuccess)
        {
            AddResult(results, new FleetApplyResult("squad", squad.Name, squad.Id, "deleted", "Extra empty squad deleted."), resultLock);
            return true;
        }

        AddResult(results, new FleetApplyResult("squad", squad.Name, squad.Id, "failed ESI error", deleteSquad.Error), resultLock);
        return false;
    }

    private static void AddResult(List<FleetApplyResult> results, FleetApplyResult result, object? resultLock = null)
    {
        if (resultLock == null)
        {
            results.Add(result);
            return;
        }

        lock (resultLock)
        {
            results.Add(result);
        }
    }

    private static object CreateInvitation(long characterId, string role, long wingId, long squadId)
    {
        return role switch
        {
            "fleet_commander" => new { character_id = characterId, role },
            "wing_commander" => new { character_id = characterId, role, wing_id = wingId },
            "squad_commander" => new { character_id = characterId, role, wing_id = wingId, squad_id = squadId },
            _ => new { character_id = characterId, role = "squad_member", wing_id = wingId, squad_id = squadId },
        };
    }

    private static object CreateMovement(string role, long wingId, long squadId)
    {
        return role switch
        {
            "fleet_commander" => new { role },
            "wing_commander" => new { role, wing_id = wingId },
            "squad_commander" => new { role, wing_id = wingId, squad_id = squadId },
            _ => new { role = "squad_member", wing_id = wingId, squad_id = squadId },
        };
    }

    private static bool MemberMatchesTarget(EsiFleetMember member, string role, LivePlacementTarget target)
    {
        role = NormalizeRole(role);
        var liveRole = NormalizeRole(member.Role);
        if (!string.Equals(liveRole, role, StringComparison.Ordinal)) return false;

        return role switch
        {
            "fleet_commander" => true,
            "wing_commander" => member.WingId == target.WingId,
            "squad_commander" => member.WingId == target.WingId && member.SquadId == target.SquadId,
            _ => member.WingId == target.WingId && member.SquadId == target.SquadId,
        };
    }

    private LiveFleetLayoutMap LiveLayoutFor(long fleetId, string profileId)
    {
        var layout = _state.LiveLayouts.FirstOrDefault(item =>
            item.FleetId == fleetId &&
            string.Equals(item.ProfileId, profileId, StringComparison.Ordinal)
        );
        if (layout != null) return layout.Normalize();

        layout = new LiveFleetLayoutMap
        {
            FleetId = fleetId,
            ProfileId = profileId,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
        _state.LiveLayouts.Add(layout);
        return layout;
    }

    private static bool SetLiveLayoutId(Dictionary<string, long> ids, string key, long value)
    {
        if (value <= 0) return false;
        if (ids.TryGetValue(key, out var existing) && existing == value) return false;
        ids[key] = value;
        return true;
    }

    private static bool PruneLiveLayout(FleetProfile profile, LiveFleetLayoutMap layout)
    {
        var changed = false;
        var validWingKeys = profile.Wings.Select((_, index) => WingStructureKey(index)).ToHashSet(StringComparer.Ordinal);
        var validSquadKeys = profile.Wings
            .SelectMany((wing, wingIndex) => wing.Squads.Select((_, squadIndex) => StructureKey(wingIndex, squadIndex)))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var key in layout.WingIds.Keys.ToList())
        {
            if (validWingKeys.Contains(key)) continue;
            layout.WingIds.Remove(key);
            changed = true;
        }

        foreach (var key in layout.SquadIds.Keys.ToList())
        {
            if (validSquadKeys.Contains(key)) continue;
            layout.SquadIds.Remove(key);
            changed = true;
        }

        return changed;
    }

    private static string WingStructureKey(int wingIndex) => $"{wingIndex}";
    private static string StructureKey(int wingIndex, int squadIndex) => $"{wingIndex}:{squadIndex}";

    private static ProfileValidation ValidateProfile(FleetProfile profile)
    {
        var validation = new ProfileValidation();
        if (string.IsNullOrWhiteSpace(profile.Name)) validation.Errors.Add("Profile name is required.");
        if (profile.Wings.Count == 0) validation.Errors.Add("Add at least one wing.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var wing in profile.Wings)
        {
            var wingName = NormalizeFleetStructureName(wing.Name);
            if (string.IsNullOrWhiteSpace(wingName)) validation.Errors.Add("Every wing needs a name.");
            if (wingName.Length > FleetStructureNameMaxLength)
            {
                validation.Errors.Add($"Wing '{wingName}' is {wingName.Length} characters. ESI wing names must be {FleetStructureNameMaxLength} characters or fewer.");
            }
            if (wing.Squads.Count == 0) validation.Warnings.Add($"Wing '{wingName}' has no squads.");
            foreach (var squad in wing.Squads)
            {
                var squadName = NormalizeFleetStructureName(squad.Name);
                if (string.IsNullOrWhiteSpace(squadName)) validation.Errors.Add($"Every squad in '{wingName}' needs a name.");
                if (squadName.Length > FleetStructureNameMaxLength)
                {
                    validation.Errors.Add($"Squad '{squadName}' in wing '{wingName}' is {squadName.Length} characters. ESI squad names must be {FleetStructureNameMaxLength} characters or fewer.");
                }
                foreach (var member in squad.Members)
                {
                    var name = member.CharacterName.Trim();
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        validation.Warnings.Add($"Blank member row in {wingName} / {squadName} will be ignored.");
                        continue;
                    }

                    if (!seen.Add(name)) validation.Errors.Add($"Duplicate character name: {name}");
                    var role = NormalizeRole(member.Role);
                    if (role == "fleet_commander")
                    {
                        validation.Warnings.Add($"{name} is set as fleet_commander; wing/squad placement is ignored by ESI for that invite.");
                    }
                }
            }
        }

        return validation;
    }

    private static string NormalizeFleetStructureName(string? name) => (name ?? string.Empty).Trim();

    private async Task<Dictionary<string, long>> ResolveCharacterIdsAsync(IReadOnlyList<string> names, bool refresh)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var name in names)
        {
            if (!refresh && _state.CharacterCache.TryGetValue(name, out var cached) && cached.CharacterId > 0)
            {
                result[name] = cached.CharacterId;
            }
            else
            {
                missing.Add(name);
            }
        }

        if (missing.Count > 0)
        {
            var response = await SendEsiAsync<UniverseIdsResponse>(HttpMethod.Post, "/universe/ids/", token: null, body: missing);
            response.ThrowIfFailed();
            foreach (var character in response.Value?.Characters ?? new List<UniverseIdName>())
            {
                if (string.IsNullOrWhiteSpace(character.Name) || character.Id <= 0) continue;
                result[character.Name] = character.Id;
                _state.CharacterCache[character.Name] = new CachedCharacter
                {
                    CharacterId = character.Id,
                    CharacterName = character.Name,
                    ResolvedUtc = DateTimeOffset.UtcNow,
                };
            }
            _state.Save();
        }

        return result;
    }

    private async Task<EsiResponse<T>> SendEsiAsync<T>(HttpMethod method, string path, string? token, object? body = null)
    {
        var url = path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : $"{EsiBaseUrl}{path}";
        var bodyJson = body == null ? null : JsonSerializer.Serialize(body, JsonOptions);
        Exception? lastException = null;

        for (var attempt = 1; attempt <= EsiTransientMaxAttempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(method, url);
                request.Headers.UserAgent.ParseAdd("TriffView/1.0 TriffFleets");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (!string.IsNullOrWhiteSpace(token))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }
                if (bodyJson != null)
                {
                    request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
                }

                using var response = await Http.SendAsync(request);
                var text = await response.Content.ReadAsStringAsync();
                var error = response.IsSuccessStatusCode ? "" : ReadError(text);
                var esiRemain = HeaderValue(response, "X-Esi-Error-Limit-Remain");
                var esiReset = HeaderValue(response, "X-Esi-Error-Limit-Reset");
                var retryAfter = HeaderValue(response, "Retry-After");
                if (!string.IsNullOrWhiteSpace(esiRemain) || !string.IsNullOrWhiteSpace(esiReset) || !string.IsNullOrWhiteSpace(retryAfter))
                {
                    error = $"{error} ESI error limit remain={esiRemain}, reset={esiReset}, retry-after={retryAfter}".Trim();
                }

                if (!response.IsSuccessStatusCode && ShouldRetryEsi(method, path, response.StatusCode, attempt))
                {
                    await Task.Delay(RetryDelay(attempt, retryAfter));
                    continue;
                }

                T? value = default;
                if (response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(text) && typeof(T) != typeof(object))
                {
                    value = JsonSerializer.Deserialize<T>(text, JsonOptions);
                }

                return new EsiResponse<T>(response.StatusCode, value, error, method.Method, path);
            }
            catch (Exception ex) when (IsTransientNetworkException(ex) && attempt < EsiTransientMaxAttempts)
            {
                lastException = ex;
                await Task.Delay(RetryDelay(attempt, ""));
            }
            catch (Exception ex) when (IsTransientNetworkException(ex))
            {
                lastException = ex;
                break;
            }
        }

        return new EsiResponse<T>(
            HttpStatusCode.ServiceUnavailable,
            default,
            lastException?.Message ?? "ESI request failed after transient retries.",
            method.Method,
            path
        );
    }

    private static bool ShouldRetryEsi(HttpMethod method, string path, HttpStatusCode statusCode, int attempt)
    {
        if (attempt >= EsiTransientMaxAttempts) return false;

        var status = (int)statusCode;
        var transient = status is 408 or 420 or 429 or 500 or 502 or 503 or 504;
        if (!transient) return false;

        if (method == HttpMethod.Get || method == HttpMethod.Put) return true;
        return method == HttpMethod.Post && path.StartsWith("/universe/ids/", StringComparison.OrdinalIgnoreCase);
    }

    private static TimeSpan RetryDelay(int attempt, string retryAfter)
    {
        if (int.TryParse(retryAfter, out var seconds) && seconds > 0)
        {
            return TimeSpan.FromSeconds(Math.Min(seconds, 8));
        }

        return TimeSpan.FromMilliseconds(EsiTransientBaseDelayMs * attempt);
    }

    private static bool IsTransientNetworkException(Exception ex)
    {
        return ex is HttpRequestException or TaskCanceledException or SocketException;
    }

    private static string HeaderValue(HttpResponseMessage response, string name)
    {
        return response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : "";
    }

    private static string ReadError(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "No response body.";
        try
        {
            var node = JsonNode.Parse(text)?.AsObject();
            return node?["error"]?.GetValue<string>() ?? text;
        }
        catch
        {
            return text;
        }
    }

    private LiveFleetInfo RequireLiveFleet()
    {
        return _liveFleet ?? throw new InvalidOperationException("Detect the fleet before building or applying a plan.");
    }

    private FleetBossAuth? SelectedBoss()
    {
        return _state.Bosses.FirstOrDefault(boss => boss.CharacterId == _state.SelectedBossCharacterId)
            ?? _state.Bosses.FirstOrDefault();
    }

    private FleetProfile? SelectedProfile()
    {
        return ProfileById(_state.SelectedProfileId) ?? _state.Profiles.FirstOrDefault();
    }

    private FleetProfile? ProfileById(string? id)
    {
        return string.IsNullOrWhiteSpace(id) ? null : _state.Profiles.FirstOrDefault(profile => profile.Id == id);
    }

    private void PostState(bool force = false)
    {
        try
        {
            _state.Normalize();
            var selectedBoss = SelectedBoss();
            var selectedProfile = SelectedProfile();
            var state = new
            {
                type = "trifffleets:state",
                authConfigured = !string.IsNullOrWhiteSpace(ClientId),
                authInProgress = _authInProgress,
                requiredScopes = Scopes.Split(' '),
                redirectUri = RedirectUri,
                selectedBossCharacterId = selectedBoss?.CharacterId ?? 0,
                bosses = _state.Bosses.Select(boss => new
                {
                    boss.CharacterId,
                    boss.CharacterName,
                    boss.Scopes,
                    boss.AuthenticatedUtc,
                    tokenStored = !string.IsNullOrWhiteSpace(CredentialStore.Read(RefreshTokenTarget(boss.CharacterId))),
                }).ToArray(),
                liveFleet = _liveFleet,
                selectedProfileId = selectedProfile?.Id ?? "",
                profiles = _state.Profiles,
                dryRun = _lastPlan,
                applyResult = _lastApply,
                complianceNote = "Uses ESI only. Does not control EVE clients. Characters accept invites manually in-game.",
            };
            var json = JsonSerializer.Serialize(state, JsonOptions);
            if (!force && string.Equals(json, _lastPostedStateJson, StringComparison.Ordinal)) return;
            _lastPostedStateJson = json;
            _postToHud(state);
        }
        catch (Exception ex)
        {
            PostError("state", ex.Message);
        }
    }

    private void PostError(string action, string message)
    {
        _postToHud(new
        {
            type = "trifffleets:error",
            action,
            message,
        });
    }

    private static string NormalizeRole(string? role)
    {
        return role is "fleet_commander" or "wing_commander" or "squad_commander" or "squad_member"
            ? role
            : "squad_member";
    }

    private static string RefreshTokenTarget(long characterId) => $"TriffView.TriffFleets.RefreshToken.{characterId}";

    private static string Base64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }

    private static EveJwtIdentity DecodeEveJwt(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2) throw new InvalidDataException("EVE SSO returned an invalid access token.");
        var payload = Encoding.UTF8.GetString(DecodeBase64Url(parts[1]));
        var node = JsonNode.Parse(payload)?.AsObject() ?? throw new InvalidDataException("EVE SSO access token payload could not be read.");
        var characterId = 0L;

        foreach (var key in new[] { "character_id", "CharacterID", "characterID", "characterId" })
        {
            if (node[key] == null) continue;
            try
            {
                characterId = node[key]!.GetValue<long>();
                if (characterId > 0) break;
            }
            catch
            {
                var value = node[key]?.GetValue<string>() ?? "";
                long.TryParse(value, out characterId);
                if (characterId > 0) break;
            }
        }

        if (characterId <= 0)
        {
            var sub = node["sub"]?.GetValue<string>() ?? "";
            var numericTail = sub.Split(':', '/', '|').LastOrDefault(part => long.TryParse(part, out _)) ?? "";
            long.TryParse(numericTail, out characterId);
        }

        if (characterId <= 0)
        {
            throw new InvalidDataException("The EVE SSO token did not include a usable character ID.");
        }

        var name = node["name"]?.GetValue<string>() ?? $"Character {characterId}";
        var scopes = new List<string>();
        if (node["scp"] is JsonArray array)
        {
            scopes.AddRange(array.Select(scope => scope?.GetValue<string>() ?? "").Where(scope => !string.IsNullOrWhiteSpace(scope)));
        }
        else if (node["scp"] != null)
        {
            scopes.AddRange((node["scp"]?.GetValue<string>() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        return new EveJwtIdentity(characterId, name, scopes);
    }
}

internal sealed class TriffFleetsLocalState
{
    public long SelectedBossCharacterId { get; set; }
    public string SelectedProfileId { get; set; } = "";
    public List<FleetBossAuth> Bosses { get; set; } = new();
    public List<FleetProfile> Profiles { get; set; } = new();
    public Dictionary<string, CachedCharacter> CharacterCache { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<LiveFleetLayoutMap> LiveLayouts { get; set; } = new();

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TriffView",
        "trifffleets-settings.json"
    );

    public static TriffFleetsLocalState Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new TriffFleetsLocalState().Normalize();
            var state = JsonSerializer.Deserialize<TriffFleetsLocalState>(File.ReadAllText(SettingsPath), TriffFleetsControllerJson.Options);
            return (state ?? new TriffFleetsLocalState()).Normalize();
        }
        catch
        {
            return new TriffFleetsLocalState().Normalize();
        }
    }

    public void Save()
    {
        Normalize();
        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, TriffFleetsControllerJson.Options), new UTF8Encoding(false));
    }

    public TriffFleetsLocalState Normalize()
    {
        CharacterCache = new Dictionary<string, CachedCharacter>(CharacterCache ?? new Dictionary<string, CachedCharacter>(), StringComparer.OrdinalIgnoreCase);
        Bosses ??= new List<FleetBossAuth>();
        Bosses = Bosses
            .Where(boss => boss.CharacterId > 0)
            .GroupBy(boss => boss.CharacterId)
            .Select(group => group.OrderByDescending(boss => boss.AuthenticatedUtc).First())
            .ToList();
        Profiles ??= new List<FleetProfile>();
        Profiles = Profiles.Select(profile => profile.Normalize()).ToList();
        LiveLayouts ??= new List<LiveFleetLayoutMap>();
        LiveLayouts = LiveLayouts
            .Select(layout => layout.Normalize())
            .Where(layout => layout.FleetId > 0 && !string.IsNullOrWhiteSpace(layout.ProfileId))
            .GroupBy(layout => $"{layout.FleetId}:{layout.ProfileId}", StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(layout => layout.UpdatedUtc).First())
            .OrderByDescending(layout => layout.UpdatedUtc)
            .Take(20)
            .ToList();
        if (Profiles.Count == 0) Profiles.Add(FleetProfile.Default("Default Fleet"));
        if (string.IsNullOrWhiteSpace(SelectedProfileId) || Profiles.All(profile => profile.Id != SelectedProfileId))
        {
            SelectedProfileId = Profiles.First().Id;
        }
        if (SelectedBossCharacterId <= 0 || Bosses.All(boss => boss.CharacterId != SelectedBossCharacterId))
        {
            SelectedBossCharacterId = Bosses.FirstOrDefault()?.CharacterId ?? 0;
        }
        return this;
    }
}

internal static class TriffFleetsControllerJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
}

internal sealed class FleetBossAuth
{
    public long CharacterId { get; set; }
    public string CharacterName { get; set; } = "";
    public List<string> Scopes { get; set; } = new();
    public DateTimeOffset AuthenticatedUtc { get; set; }
}

internal sealed class CachedCharacter
{
    public long CharacterId { get; set; }
    public string CharacterName { get; set; } = "";
    public DateTimeOffset ResolvedUtc { get; set; }
}

internal sealed class LiveFleetLayoutMap
{
    public long FleetId { get; set; }
    public string ProfileId { get; set; } = "";
    public DateTimeOffset UpdatedUtc { get; set; }
    public Dictionary<string, long> WingIds { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, long> SquadIds { get; set; } = new(StringComparer.Ordinal);

    public LiveFleetLayoutMap Normalize()
    {
        ProfileId = ProfileId?.Trim() ?? "";
        WingIds = new Dictionary<string, long>(WingIds ?? new Dictionary<string, long>(), StringComparer.Ordinal);
        SquadIds = new Dictionary<string, long>(SquadIds ?? new Dictionary<string, long>(), StringComparer.Ordinal);
        if (UpdatedUtc == default) UpdatedUtc = DateTimeOffset.UtcNow;
        return this;
    }
}

internal sealed class FleetProfile
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Default Fleet";
    public string Description { get; set; } = "";
    public bool KeepExistingMembersInFleet { get; set; } = true;
    public List<FleetWingProfile> Wings { get; set; } = new();

    public static FleetProfile Default(string name)
    {
        return new FleetProfile
        {
            Name = name,
            Description = "",
            Wings = new List<FleetWingProfile>
            {
                new()
                {
                    Name = "DPS Wing",
                    Squads = new List<FleetSquadProfile>
                    {
                        new() { Name = "Main DPS", Members = new List<FleetMemberProfile>() },
                    },
                },
            },
        }.Normalize();
    }

    public FleetProfile Normalize()
    {
        Version = Version <= 0 ? 1 : Version;
        if (string.IsNullOrWhiteSpace(Id)) Id = Guid.NewGuid().ToString("N");
        Name = string.IsNullOrWhiteSpace(Name) ? "Fleet Profile" : Name.Trim();
        Description = Description?.Trim() ?? "";
        Wings ??= new List<FleetWingProfile>();
        foreach (var wing in Wings) wing.Normalize();
        return this;
    }

    public IEnumerable<FleetMemberProfile> AllMembers()
    {
        return Wings.SelectMany(wing => wing.Squads).SelectMany(squad => squad.Members);
    }

    public IEnumerable<MemberPlacement> MemberPlacements()
    {
        for (var wingIndex = 0; wingIndex < Wings.Count; wingIndex++)
        {
            var wing = Wings[wingIndex];
            for (var squadIndex = 0; squadIndex < wing.Squads.Count; squadIndex++)
            {
                var squad = wing.Squads[squadIndex];
                foreach (var member in squad.Members)
                {
                    yield return new MemberPlacement(wingIndex, squadIndex, wing.Name, squad.Name, member);
                }
            }
        }
    }
}

internal sealed class FleetWingProfile
{
    public string Name { get; set; } = "Wing";
    public List<FleetSquadProfile> Squads { get; set; } = new();

    public void Normalize()
    {
        Name = string.IsNullOrWhiteSpace(Name) ? "Wing" : Name.Trim();
        Squads ??= new List<FleetSquadProfile>();
        foreach (var squad in Squads) squad.Normalize();
    }
}

internal sealed class FleetSquadProfile
{
    public string Name { get; set; } = "Squad";
    public List<FleetMemberProfile> Members { get; set; } = new();

    public void Normalize()
    {
        Name = string.IsNullOrWhiteSpace(Name) ? "Squad" : Name.Trim();
        Members ??= new List<FleetMemberProfile>();
        foreach (var member in Members) member.Normalize();
    }
}

internal sealed class FleetMemberProfile
{
    public string CharacterName { get; set; } = "";
    public string Role { get; set; } = "squad_member";

    public void Normalize()
    {
        CharacterName = CharacterName?.Trim() ?? "";
        Role = Role is "fleet_commander" or "wing_commander" or "squad_commander" or "squad_member" ? Role : "squad_member";
    }
}

internal sealed class LiveFleetInfo
{
    public long FleetId { get; set; }
    public long FleetBossId { get; set; }
    public string Role { get; set; } = "";
    public long WingId { get; set; }
    public long SquadId { get; set; }
    public bool CanModify { get; set; }
    public DateTimeOffset DetectedUtc { get; set; }
}

internal sealed class FleetDryRunPlan
{
    public DateTimeOffset GeneratedUtc { get; set; }
    public long FleetId { get; set; }
    public string ProfileId { get; set; } = "";
    public string ProfileName { get; set; } = "";
    public bool CanApply { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public List<FleetStructureAction> WingsToCreate { get; set; } = new();
    public List<FleetRenameAction> WingsToRename { get; set; } = new();
    public List<FleetStructureAction> SquadsToCreate { get; set; } = new();
    public List<FleetRenameAction> SquadsToRename { get; set; } = new();
    public List<FleetInvitePlan> Invites { get; set; } = new();
    public List<FleetInvitePlan> AlreadyInFleet { get; set; } = new();
    public List<string> UnresolvedCharacters { get; set; } = new();
    public List<string> DuplicateCharacters { get; set; } = new();
}

internal sealed record FleetStructureAction(int Position, string Name, string? ParentName);
internal sealed record FleetRenameAction(long Id, string From, string To);
internal sealed record FleetInvitePlan(string CharacterName, long CharacterId, string Role, string WingName, string SquadName);
internal sealed record FleetApplySummary(DateTimeOffset AppliedUtc, List<FleetApplyResult> Results)
{
    public FleetApplySummary() : this(DateTimeOffset.UtcNow, new List<FleetApplyResult>()) { }
}
internal sealed record FleetApplyResult(string Kind, string Name, long Id, string Status, string Detail)
{
    public static FleetApplyResult Member(string name, long id, string status, string detail) => new("member", name, id, status, detail);
}
internal sealed record MemberPlacement(int WingIndex, int SquadIndex, string WingName, string SquadName, FleetMemberProfile Member);
internal sealed record LivePlacementTarget(long WingId, long SquadId);
internal sealed record FleetStructureResult(Dictionary<string, LivePlacementTarget> Targets, List<EsiWing> LiveWings, bool Mutated);
internal sealed record ProfileValidation(List<string> Errors, List<string> Warnings)
{
    public ProfileValidation() : this(new List<string>(), new List<string>()) { }
}
internal sealed record AccessTokenCache(string AccessToken, DateTimeOffset ExpiresUtc);
internal sealed record EveJwtIdentity(long CharacterId, string CharacterName, List<string> Scopes);

internal sealed class TokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = "";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = "";
}

internal sealed class CharacterFleetInfo
{
    [JsonPropertyName("fleet_boss_id")]
    public long FleetBossId { get; set; }

    [JsonPropertyName("fleet_id")]
    public long FleetId { get; set; }

    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("squad_id")]
    public long SquadId { get; set; }

    [JsonPropertyName("wing_id")]
    public long WingId { get; set; }
}

internal sealed class EsiWing
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("squads")]
    public List<EsiSquad> Squads { get; set; } = new();
}

internal sealed class EsiSquad
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

internal sealed class EsiFleetMember
{
    [JsonPropertyName("character_id")]
    public long CharacterId { get; set; }

    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("wing_id")]
    public long WingId { get; set; }

    [JsonPropertyName("squad_id")]
    public long SquadId { get; set; }
}

internal sealed class CreateWingResponse
{
    [JsonPropertyName("wing_id")]
    public long WingId { get; set; }
}

internal sealed class CreateSquadResponse
{
    [JsonPropertyName("squad_id")]
    public long SquadId { get; set; }
}

internal sealed class UniverseIdsResponse
{
    [JsonPropertyName("characters")]
    public List<UniverseIdName> Characters { get; set; } = new();
}

internal sealed class UniverseIdName
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

internal sealed record EsiResponse<T>(HttpStatusCode StatusCode, T? Value, string Error, string Method, string Path)
{
    public bool IsSuccess => (int)StatusCode is >= 200 and <= 299;

    public void ThrowIfFailed()
    {
        if (!IsSuccess)
        {
            throw new InvalidOperationException($"{Method} {Path} returned {(int)StatusCode}: {Error}");
        }
    }
}

internal static class CredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;

    public static void Write(string target, string secret)
    {
        var bytes = Encoding.Unicode.GetBytes(secret);
        var blob = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = "TriffView",
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new InvalidOperationException($"Windows Credential Manager write failed: {Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(blob);
        }
    }

    public static string Read(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out var credentialPtr) || credentialPtr == IntPtr.Zero)
        {
            return "";
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPtr);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return "";
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    public static void Delete(string target)
    {
        CredDelete(target, CredTypeGeneric, 0);
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential userCredential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }
}
