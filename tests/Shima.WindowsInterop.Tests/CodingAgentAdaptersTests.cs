using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using FluentAssertions;
using NSubstitute;
using Shima.AgentBridge;
using Shima.Core.Contracts;
using Shima.Core.Shell;
using Shima.Widgets.Developer;
using Shima.Widgets.Developer.Views;
using Shima.WindowsInterop.Agents;
using Xunit;

namespace Shima.WindowsInterop.Tests;

public sealed class CodingAgentAdaptersTests : IDisposable
{
    private readonly string _tempRoot;

    public CodingAgentAdaptersTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "shima-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task AgentBridge_WhenShimaClosed_FailsOpenWithEmptyStdoutExitZeroFast()
    {
        // Warm up AgentBridgeRunner pipeline before timing closed-pipe fail-open latency.
        _ = await AgentBridgeRunner.RunAsync(
            new StringReader("""{"session_id":"warm","hook_event_name":"Stop"}"""),
            new StringWriter(),
            pipeName: $"shima-warm-{Guid.NewGuid():N}",
            connectTimeoutMs: 1,
            responseTimeoutMs: 100);

        var stdinJson = """
            {"session_id":"s1","cwd":"C:\\repo","hook_event_name":"PermissionRequest","tool_name":"Bash","tool_input":{"command":"npm test"}}
            """;
        using var stdin = new StringReader(stdinJson);
        using var stdout = new StringWriter();

        var sw = Stopwatch.StartNew();
        var exitCode = await AgentBridgeRunner.RunAsync(
            stdin,
            stdout,
            pipeName: $"shima-nonexistent-{Guid.NewGuid():N}",
            connectTimeoutMs: AgentBridgeRunner.DefaultConnectTimeoutMs,
            responseTimeoutMs: 5000);
        sw.Stop();

        exitCode.Should().Be(0);
        stdout.ToString().Should().BeEmpty();
        sw.ElapsedMilliseconds.Should().BeLessThan(50);
    }

    [Fact]
    public async Task AgentBridge_WhenZeroTimeoutOrPipeDisconnectsMidPrompt_FailsOpenWithEmptyStdout()
    {
        using var zeroStdin = new StringReader("""{"session_id":"s1","hook_event_name":"PreToolUse","tool_name":"Write"}""");
        using var zeroStdout = new StringWriter();
        var zeroCode = await AgentBridgeRunner.RunAsync(
            zeroStdin,
            zeroStdout,
            pipeName: $"shima-zero-{Guid.NewGuid():N}",
            connectTimeoutMs: 25,
            responseTimeoutMs: 0);
        zeroCode.Should().Be(0);
        zeroStdout.ToString().Should().BeEmpty();

        // Simulate pipe closing mid-prompt before decision arrives.
        var pipeName = $"shima-drop-{Guid.NewGuid():N}";
        using var server = NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            1024,
            1024,
            ClaudeCodeAdapter.CreatePipeSecurity());

        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            using var reader = new StreamReader(server, leaveOpen: true);
            _ = await reader.ReadLineAsync();
            server.Disconnect();
        });

        using var dropStdin = new StringReader("""{"session_id":"s2","hook_event_name":"PermissionRequest","tool_name":"Bash"}""");
        using var dropStdout = new StringWriter();
        var dropCode = await AgentBridgeRunner.RunAsync(
            dropStdin,
            dropStdout,
            pipeName,
            connectTimeoutMs: 500,
            responseTimeoutMs: 5000);

        await serverTask;
        dropCode.Should().Be(0);
        dropStdout.ToString().Should().BeEmpty();
    }

    [Fact]
    public void ClaudeCodeAdapter_PipeSecurity_RestrictsAccessStrictlyToCurrentUserSid()
    {
        var security = ClaudeCodeAdapter.CreatePipeSecurity();
        security.AreAccessRulesProtected.Should().BeTrue();

        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToArray();

        using var identity = WindowsIdentity.GetCurrent();
        rules.Should().ContainSingle();
        rules[0].IdentityReference.Should().Be(identity.User);
        rules[0].AccessControlType.Should().Be(AccessControlType.Allow);
        rules.Should().NotContain(r => r.IdentityReference.Value == "S-1-5-18"); // No NT AUTHORITY\SYSTEM
        rules.Should().NotContain(r => r.IdentityReference.Value == "S-1-1-0");  // No Everyone
    }

    [Fact]
    public async Task ClaudeCodeAdapter_HookInstaller_BacksUpPreservesExistingAndUninstallsCleanly()
    {
        var settingsPath = Path.Combine(_tempRoot, ".claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        var originalSettings = """
            {
              "theme": "dark",
              "permissions": { "allow": ["Read"] },
              "hooks": {
                "PreToolUse": [
                  {
                    "matcher": "Edit",
                    "hooks": [{ "type": "command", "command": "custom-lint.exe" }]
                  }
                ]
              }
            }
            """;
        await File.WriteAllTextAsync(settingsPath, originalSettings);

        var prefs = new FakeAppPreferencesService(new AppPreferencesSnapshot { ClaudeCodeOptInEnabled = true });
        await using var adapter = new ClaudeCodeAdapter(
            prefs,
            logger: null,
            pipeName: $"shima-hooks-{Guid.NewGuid():N}",
            settingsFilePath: settingsPath,
            windowFocuser: new FakeTerminalWindowFocuser(),
            utcNowProvider: null);

        adapter.IsHookInstalled().Should().BeFalse();

        var installResult = await adapter.InstallHooksAsync(@"C:\Shima\Shima.AgentBridge.exe");
        installResult.Success.Should().BeTrue();
        installResult.BackupPath.Should().NotBeNullOrWhiteSpace();
        File.Exists(installResult.BackupPath!).Should().BeTrue();
        adapter.IsHookInstalled().Should().BeTrue();

        // Install again -> idempotent without duplicating Shima entries
        await adapter.InstallHooksAsync(@"C:\Shima\Shima.AgentBridge.exe");
        var installedDoc = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
        installedDoc.RootElement.GetProperty("theme").GetString().Should().Be("dark");
        installedDoc.RootElement.GetProperty("permissions").GetProperty("allow")[0].GetString().Should().Be("Read");
        var preToolEntries = installedDoc.RootElement.GetProperty("hooks").GetProperty("PreToolUse").EnumerateArray().ToArray();
        preToolEntries.Should().HaveCount(2); // 1 custom + 1 Shima

        var uninstallResult = await adapter.UninstallHooksAsync();
        uninstallResult.Success.Should().BeTrue();
        adapter.IsHookInstalled().Should().BeFalse();

        var afterUninstallDoc = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
        afterUninstallDoc.RootElement.GetProperty("theme").GetString().Should().Be("dark");
        var remaining = afterUninstallDoc.RootElement.GetProperty("hooks").GetProperty("PreToolUse").EnumerateArray().ToArray();
        remaining.Should().ContainSingle();
        remaining[0].GetProperty("hooks")[0].GetProperty("command").GetString().Should().Be("custom-lint.exe");
    }

    [Fact]
    public async Task ClaudeCodeAdapter_EndToEndPipe_HandlesAllowDenyNotificationAndStop()
    {
        var pipeName = $"shima-cc-e2e-{Guid.NewGuid():N}";
        var settingsPath = Path.Combine(_tempRoot, ".claude", "settings.json");
        var prefs = new FakeAppPreferencesService(new AppPreferencesSnapshot { ClaudeCodeOptInEnabled = false });
        var focuser = new FakeTerminalWindowFocuser { FocusResult = true };

        await using var adapter = new ClaudeCodeAdapter(
            prefs,
            logger: null,
            pipeName,
            settingsPath,
            focuser,
            utcNowProvider: null);

        // Opt-in disabled -> StartAsync leaves adapter Disconnected
        await adapter.StartAsync();
        adapter.ConnectionState.Should().Be(AgentConnectionState.Disconnected);

        // Opt in -> adapter starts pipe listener
        await prefs.SaveAsync(new AppPreferencesSnapshot { ClaudeCodeOptInEnabled = true });
        await WaitUntilAsync(() => adapter.ConnectionState == AgentConnectionState.Connected);

        var updates = new ConcurrentQueue<AgentSessionSnapshot>();
        adapter.SessionUpdated += (_, snap) => updates.Enqueue(snap);

        // 1. PermissionRequest -> Allow -> Bridge emits allow JSON & sends decision_emitted ack
        var allowTcs = new TaskCompletionSource<AgentSessionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.SessionUpdated += (_, snap) =>
        {
            if (snap.SessionId == "claude-code:ses_1" && snap.Status == AgentStatus.AwaitingApproval)
            {
                allowTcs.TrySetResult(snap);
            }
        };

        using var allowOut = new StringWriter();
        var allowBridgeTask = AgentBridgeRunner.RunAsync(
            new StringReader("""{"session_id":"ses_1","cwd":"C:\\repos\\shima","hook_event_name":"PermissionRequest","tool_name":"Bash","tool_input":{"command":"dotnet test"}}"""),
            allowOut,
            pipeName,
            connectTimeoutMs: 1000,
            responseTimeoutMs: 5000,
            processIdOverride: 9876);

        var awaiting = await allowTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        awaiting.HasActionablePermissionRequest.Should().BeTrue();
        awaiting.PendingRequestId.Should().NotBeNullOrWhiteSpace();
        awaiting.ProcessId.Should().Be(9876);

        var allowResult = await adapter.RespondToApprovalAsync(awaiting.SessionId, awaiting.PendingRequestId!, allow: true, reason: null);
        allowResult.Confirmed.Should().BeTrue();
        allowResult.ConfirmationSource.Should().Be("claude.pipe.ack");

        (await allowBridgeTask.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(0);
        var allowJson = JsonDocument.Parse(allowOut.ToString());
        allowJson.RootElement.GetProperty("hookSpecificOutput").GetProperty("hookEventName").GetString().Should().Be("PermissionRequest");
        allowJson.RootElement.GetProperty("hookSpecificOutput").GetProperty("permissionDecision").GetString().Should().Be("allow");

        // 2. PreToolUse -> Deny with reason
        var denyTcs = new TaskCompletionSource<AgentSessionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.SessionUpdated += (_, snap) =>
        {
            if (snap.SessionId == "claude-code:ses_1" && snap.Status == AgentStatus.AwaitingApproval && snap.PendingToolName == "Write")
            {
                denyTcs.TrySetResult(snap);
            }
        };

        using var denyOut = new StringWriter();
        var denyBridgeTask = AgentBridgeRunner.RunAsync(
            new StringReader("""{"session_id":"ses_1","cwd":"C:\\repos\\shima","hook_event_name":"PreToolUse","tool_name":"Write","tool_input":{"file_path":".env"}}"""),
            denyOut,
            pipeName,
            connectTimeoutMs: 1000,
            responseTimeoutMs: 5000,
            processIdOverride: 9876);

        var denySnap = await denyTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var denyResult = await adapter.RespondToApprovalAsync(denySnap.SessionId, denySnap.PendingRequestId!, allow: false, reason: "Protected file");
        denyResult.Confirmed.Should().BeTrue();

        (await denyBridgeTask.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(0);
        var denyJson = JsonDocument.Parse(denyOut.ToString());
        denyJson.RootElement.GetProperty("hookSpecificOutput").GetProperty("permissionDecision").GetString().Should().Be("deny");
        denyJson.RootElement.GetProperty("hookSpecificOutput").GetProperty("permissionDecisionReason").GetString().Should().Be("Protected file");

        // 3. Stop -> Finished, FocusTerminal succeeds for known PID
        var stopOut = new StringWriter();
        await AgentBridgeRunner.RunAsync(
            new StringReader("""{"session_id":"ses_1","cwd":"C:\\repos\\shima","hook_event_name":"Stop","message":"Completed"}"""),
            stopOut,
            pipeName,
            connectTimeoutMs: 1000,
            responseTimeoutMs: 2000,
            processIdOverride: 9876);
        stopOut.ToString().Should().BeEmpty();
        updates.ToArray().Should().Contain(s => s.SessionId == "claude-code:ses_1" && s.Status == AgentStatus.Finished);
        (await adapter.FocusTerminalAsync("claude-code:ses_1")).Should().BeTrue();
        focuser.LastFocusedPid.Should().Be(9876);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("http://localhost:4096", true)]
    [InlineData("http://127.0.0.1:5000", true)]
    [InlineData("https://localhost:4096", false)]
    [InlineData("http://192.168.1.10:4096", false)]
    [InlineData("http://api.example.com:4096", false)]
    [InlineData("http://user:pw@localhost:4096", false)]
    [InlineData("not a url", false)]
    public void OpenCodeAdapter_TryValidateLoopbackEndpoint_EnforcesLoopbackHttpOnly(string? raw, bool expected)
    {
        var actual = OpenCodeAdapter.TryValidateLoopbackEndpoint(raw, out var uri, out _);
        actual.Should().Be(expected);
        if (expected)
        {
            uri.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task OpenCodeAdapter_DpapiPasswordAuthAndHttp401_SurfacesAuthenticationRequiredAndHaltsRetries()
    {
        var seenAuthHeaders = new ConcurrentQueue<string?>();
        var return401 = false;
        var handler = new ScriptedHttpHandler(req =>
        {
            seenAuthHeaders.Enqueue(req.Headers.Authorization?.ToString());
            if (return401)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"healthy":true}""", Encoding.UTF8, "application/json")
            };
        });

        var retryDelays = new List<TimeSpan>();
        var prefs = new FakeAppPreferencesService(new AppPreferencesSnapshot
        {
            OpenCodeOptInEnabled = true,
            OpenCodeEndpointUrl = "http://127.0.0.1:4096"
        });

        await using var adapter = new OpenCodeAdapter(
            prefs,
            logger: null,
            handler,
            delayProvider: (d, _) =>
            {
                retryDelays.Add(d);
                return Task.CompletedTask;
            },
            utcNowProvider: null);

        // Blank password -> no Authorization header
        _ = await adapter.CheckHealthAsync();
        seenAuthHeaders.TryDequeue(out var firstHeader).Should().BeTrue();
        firstHeader.Should().BeNull();

        // Configured DPAPI password -> Basic opencode:<password>
        await prefs.SaveAsync(prefs.Current with { HasOpenCodeServerPassword = true }, newOpenCodeServerPassword: "my-secret");
        seenAuthHeaders.Clear();
        _ = await adapter.CheckHealthAsync();
        seenAuthHeaders.TryDequeue(out var authHeader).Should().BeTrue();
        var expectedBasic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:my-secret"));
        authHeader.Should().Be(expectedBasic);

        // HTTP 401 -> surfaces AuthenticationRequired AND stops SSE retry loop cleanly (0 retries!)
        return401 = true;
        var health401 = await adapter.CheckHealthAsync();
        health401.Status.Should().Be(AdapterHealthStatus.AuthenticationRequired);
        adapter.ConnectionState.Should().Be(AgentConnectionState.Disconnected);

        await adapter.StartAsync();
        await Task.Delay(50);
        retryDelays.Should().BeEmpty();
        var afterStartHealth = await adapter.CheckHealthAsync();
        afterStartHealth.Status.Should().Be(AdapterHealthStatus.AuthenticationRequired);
    }

    [Fact]
    public async Task OpenCodeAdapter_FourStateMapping_Isolation_ConfirmedOnceReject_AndCapabilityDegradation()
    {
        var openApiWithReply = """
            {
              "paths": {
                "/session/{id}/permissions/{permissionID}": { "post": {} }
              }
            }
            """;
        var openApiReadOnly = """
            {
              "paths": {
                "/global/event": { "get": {} }
              }
            }
            """;

        var currentOpenApi = openApiWithReply;
        var postStatusCode = HttpStatusCode.OK;
        var postedBodies = new List<(string Path, string Body)>();

        var handler = new ScriptedHttpHandler(async req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/global/health")
            {
                return JsonOk("""{"healthy":true}""");
            }

            if (path == "/doc")
            {
                return JsonOk(currentOpenApi);
            }

            if (path == "/session")
            {
                return JsonOk("[]");
            }

            if (path == "/session/status")
            {
                return JsonOk("{}");
            }

            if (path == "/permission")
            {
                return JsonOk("[]");
            }

            if (req.Method == HttpMethod.Post)
            {
                var body = await req.Content!.ReadAsStringAsync();
                postedBodies.Add((path, body));
                return new HttpResponseMessage(postStatusCode);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var prefs = new FakeAppPreferencesService(new AppPreferencesSnapshot
        {
            OpenCodeOptInEnabled = true,
            OpenCodeEndpointUrl = "http://localhost:4096"
        });

        var now = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
        await using var adapter = new OpenCodeAdapter(
            prefs,
            logger: null,
            handler,
            delayProvider: null,
            utcNowProvider: () => now);

        (await adapter.ConnectOnceAndReconcileForTestingAsync(CancellationToken.None)).Should().BeTrue();
        adapter.ConnectionState.Should().Be(AgentConnectionState.Connected);
        adapter.Capabilities.SupportsInteractiveApproval.Should().BeTrue();
        adapter.Capabilities.SupportsTerminalFocus.Should().BeFalse(); // No PID in headless server

        var snapshots = new List<AgentSessionSnapshot>();
        adapter.SessionUpdated += (_, s) => snapshots.Add(s);

        // 1. Session A: created -> tool working -> retry (non-fatal) -> error (fatal)
        adapter.IngestEventJson("""{"type":"session.created","properties":{"info":{"id":"ses_A","directory":"C:\\repo-a","title":"Fix parser"}}}""").Should().BeTrue();
        snapshots.Last().Should().Match<AgentSessionSnapshot>(s => s.SessionId == "opencode:ses_A" && s.Status == AgentStatus.Working && !s.IsFatalError);

        adapter.IngestEventJson("""{"type":"session.status","properties":{"sessionID":"ses_A","status":{"type":"retry","message":"Retrying API"}}}""").Should().BeTrue();
        snapshots.Last().Should().Match<AgentSessionSnapshot>(s => s.Status == AgentStatus.NeedsAttention && !s.IsFatalError);

        adapter.IngestEventJson("""{"type":"session.error","properties":{"sessionID":"ses_A","error":{"message":"Provider auth failed"}}}""").Should().BeTrue();
        snapshots.Last().Should().Match<AgentSessionSnapshot>(s => s.Status == AgentStatus.NeedsAttention && s.IsFatalError);

        // 2. Concurrent Session B isolated from Session A
        adapter.IngestEventJson("""{"type":"permission.asked","properties":{"id":"perm_B1","sessionID":"ses_B","permission":"bash","title":"Run tests","directory":"C:\\repo-b"}}""").Should().BeTrue();
        snapshots.Last().Should().Match<AgentSessionSnapshot>(s =>
            s.SessionId == "opencode:ses_B"
            && s.Status == AgentStatus.AwaitingApproval
            && s.PendingRequestId == "perm_B1"
            && s.HasActionablePermissionRequest);

        // Ambiguous payload without sessionID or permissionID is ignored cleanly
        adapter.IngestEventJson("""{"type":"session.status","properties":{"status":{"type":"busy"}}}""").Should().BeFalse();
        adapter.AmbiguousEventsIgnoredCount.Should().Be(1);

        // Cross-session approval rejection
        var crossSession = await adapter.RespondToApprovalAsync("opencode:ses_A", "perm_B1", allow: true, reason: null);
        crossSession.Confirmed.Should().BeFalse();

        // Confirmed-only Allow strictly sends "once" (never "always")
        var allowRes = await adapter.RespondToApprovalAsync("opencode:ses_B", "perm_B1", allow: true, reason: null);
        allowRes.Confirmed.Should().BeTrue();
        allowRes.ConfirmationSource.Should().Be("opencode.http.replied");
        postedBodies.Should().ContainSingle();
        postedBodies[0].Path.Should().Be("/session/ses_B/permissions/perm_B1");
        postedBodies[0].Body.Should().Contain("\"response\":\"once\"");
        postedBodies[0].Body.Should().NotContain("always");
        snapshots.Last().Status.Should().Be(AgentStatus.Working);
        snapshots.Last().PendingRequestId.Should().BeNull();

        // Next permission request -> HTTP 500 rejects confirmation WITHOUT optimistic state flip
        adapter.IngestEventJson("""{"type":"permission.asked","properties":{"id":"perm_B2","sessionID":"ses_B","permission":"edit","title":"Edit file"}}""").Should().BeTrue();
        postStatusCode = HttpStatusCode.InternalServerError;
        var failedDeny = await adapter.RespondToApprovalAsync("opencode:ses_B", "perm_B2", allow: false, reason: "Unsafe");
        failedDeny.Confirmed.Should().BeFalse();
        snapshots.Last().Status.Should().Be(AgentStatus.AwaitingApproval);

        // Success Deny -> strictly sends "reject"
        postStatusCode = HttpStatusCode.OK;
        var okDeny = await adapter.RespondToApprovalAsync("opencode:ses_B", "perm_B2", allow: false, reason: "No edits");
        okDeny.Confirmed.Should().BeTrue();
        postedBodies.Last().Body.Should().Contain("\"response\":\"reject\"");

        // Terminal Sync: permission.replied resolves Session B without UI click
        adapter.IngestEventJson("""{"type":"permission.asked","properties":{"id":"perm_B3","sessionID":"ses_B","permission":"bash","title":"Clean"}}""").Should().BeTrue();
        adapter.IngestEventJson("""{"type":"permission.replied","properties":{"permissionID":"perm_B3"}}""").Should().BeTrue();
        snapshots.Last().Should().Match<AgentSessionSnapshot>(s => s.Status == AgentStatus.Working && s.PendingRequestId == null);

        // Idle finishes cleanly
        adapter.IngestEventJson("""{"type":"session.idle","properties":{"sessionID":"ses_B"}}""").Should().BeTrue();
        snapshots.Last().Status.Should().Be(AgentStatus.Finished);

        // Read-only OpenAPI /doc disables Allow/Deny capability cleanly
        currentOpenApi = openApiReadOnly;
        await adapter.CheckHealthAsync();
        adapter.Capabilities.SupportsInteractiveApproval.Should().BeFalse();
        adapter.Capabilities.UnsupportedCapabilities.Should().NotBeEmpty();
        adapter.IngestEventJson("""{"type":"permission.asked","properties":{"id":"perm_ro","sessionID":"ses_B","permission":"bash","title":"ReadOnly"}}""").Should().BeTrue();
        snapshots.Last().HasActionablePermissionRequest.Should().BeFalse();

        // Plugin installer & uninstaller
        var pluginPath = Path.Combine(_tempRoot, ".config", "opencode", "plugins", "shima-bridge.ts");
        await using var pluginAdapter = new OpenCodeAdapter(prefs, null, handler, null, null, pluginFilePath: pluginPath);
        pluginAdapter.IsPluginInstalled().Should().BeFalse();
        var installed = await pluginAdapter.InstallPluginAsync();
        installed.Success.Should().BeTrue();
        pluginAdapter.IsPluginInstalled().Should().BeTrue();
        var pluginSource = await File.ReadAllTextAsync(pluginPath);
        pluginSource.Should().Contain("id: \"shima-bridge\"");
        pluginSource.Should().Contain("async setup(ctx)");
        var uninstalled = await pluginAdapter.UninstallPluginAsync();
        uninstalled.Success.Should().BeTrue();
        pluginAdapter.IsPluginInstalled().Should().BeFalse();
    }

    [Fact]
    public async Task OpenCodeAdapter_ReconnectBackoff_Dedup_SnapshotRecovery_ExpiryAndAdapterIndependence()
    {
        var now = new DateTimeOffset(2026, 11, 22, 14, 0, 0, TimeSpan.Zero);
        var createdMs = now.AddSeconds(-10).ToUnixTimeMilliseconds();
        var updatedMs = now.ToUnixTimeMilliseconds();
        var ssePass = 0;
        var sessionListJson =
            $$$"""[{"id":"ses_rec","directory":"C:\\repo","title":"Migrating","time":{"created":{{{createdMs}}},"updated":{{{updatedMs}}}}}]""";
        var statusJson = """{"ses_rec":{"type":"busy"}}""";

        var handler = new ScriptedHttpHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/global/health")
            {
                return JsonOk("""{"healthy":true}""");
            }

            if (path == "/doc")
            {
                return JsonOk("""{"paths":{"/session/{id}/permissions/{permissionID}":{"post":{}}}}""");
            }

            if (path == "/session")
            {
                return JsonOk(sessionListJson);
            }

            if (path == "/session/status")
            {
                return JsonOk(statusJson);
            }

            if (path == "/permission")
            {
                return JsonOk("[]");
            }

            if (path == "/global/event")
            {
                ssePass++;
                if (ssePass == 1)
                {
                    // Pass 1: emit duplicate events then drop connection cleanly mid-turn
                    var dup1 = """data: {"type":"session.created","properties":{"info":{"id":"ses_rec","directory":"C:\\repo","title":"Migrating"}}}""" + "\n\n";
                    var dup2 = dup1;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(dup1 + dup2, Encoding.UTF8, "text/event-stream")
                    };
                }

                // Force failure passes to observe exponential backoff before pass 3 recovers via snapshot!
                if (ssePass == 2)
                {
                    // Update snapshot to idle (simulating missed session.idle while disconnected)
                    statusJson = "{}";
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }

                // Keep SSE connection open after delivering pass 3 snapshot reconciliation until stopped
                var openResp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new KeepOpenSseContent(": keepalive\n\n")
                };
                openResp.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
                return openResp;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var backoffDelays = new List<TimeSpan>();
        var prefs = new FakeAppPreferencesService(new AppPreferencesSnapshot
        {
            OpenCodeOptInEnabled = true,
            ClaudeCodeOptInEnabled = true,
            OpenCodeEndpointUrl = "http://localhost:4096"
        });

        await using var openCode = new OpenCodeAdapter(
            prefs,
            logger: null,
            handler,
            delayProvider: (delay, ct) =>
            {
                backoffDelays.Add(delay);
                return Task.Delay(10, ct);
            },
            utcNowProvider: () => now);

        // Keep ClaudeCodeAdapter independent in parallel
        var claudePipe = $"shima-indep-cc-{Guid.NewGuid():N}";
        await using var claude = new ClaudeCodeAdapter(prefs, null, claudePipe, Path.Combine(_tempRoot, "c.json"), new FakeTerminalWindowFocuser(), () => now);
        await claude.StartAsync();
        claude.ConnectionState.Should().Be(AgentConnectionState.Connected);

        var openCodeUpdates = new ConcurrentQueue<AgentSessionSnapshot>();
        openCode.SessionUpdated += (_, snap) => openCodeUpdates.Enqueue(snap);

        await openCode.StartAsync();
        await WaitUntilAsync(() => openCodeUpdates.Any(s => s.SessionId == "opencode:ses_rec" && s.Status == AgentStatus.Finished));

        openCode.DuplicateEventsIgnoredCount.Should().BeGreaterThanOrEqualTo(1);
        backoffDelays.Should().NotBeEmpty();
        backoffDelays[0].Should().Be(TimeSpan.FromSeconds(1));
        if (backoffDelays.Count >= 2)
        {
            backoffDelays[1].Should().Be(TimeSpan.FromSeconds(2));
        }

        // Verify mid-drop never marked ses_rec Finished prematurely while Reconnecting before pass 3 reconciliation
        openCodeUpdates.ToArray()
            .Where(s => s.ConnectionState == AgentConnectionState.Reconnecting)
            .Should().OnlyContain(s => s.Status == AgentStatus.Working && !s.HasActionablePermissionRequest);

        // Stopping/faulting OpenCode MUST NOT disrupt Claude Code
        await openCode.StopAsync();
        openCode.ConnectionState.Should().Be(AgentConnectionState.Disconnected);
        claude.ConnectionState.Should().Be(AgentConnectionState.Connected);

        // Inactive expiry keeps <15m rows and cleans >15m finished rows without touching active sessions
        openCode.PurgeInactiveSessions(now.AddMinutes(5)).Should().Be(0);
        now = now.AddMinutes(16);
        openCode.PurgeInactiveSessions(now).Should().Be(1);
    }

    [Fact]
    public async Task OpenCodeAdapter_V2NamedPipeEvents_ParseDataEnvelope_AllLifecycleStates_AndSurviveHttp401()
    {
        var now = new DateTimeOffset(2026, 11, 22, 22, 0, 0, TimeSpan.Zero);
        var handler = new ScriptedHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var prefs = new FakeAppPreferencesService(new AppPreferencesSnapshot
        {
            OpenCodeOptInEnabled = true,
            OpenCodeEndpointUrl = "http://127.0.0.1:4096"
        });

        var pluginPath = Path.Combine(_tempRoot, "v2-pipe", "plugins", "shima-bridge.ts");
        var pipeName = $"shima-oc-v2-{Guid.NewGuid():N}";
        await using var adapter = new OpenCodeAdapter(
            prefs,
            logger: null,
            handler,
            delayProvider: (_, _) => Task.CompletedTask,
            utcNowProvider: () => now,
            pluginFilePath: pluginPath,
            pluginPipeName: pipeName);

        // Install plugin and verify backup appends .bak on reinstall so OpenCode v2 never auto-loads backups
        var firstInstall = await adapter.InstallPluginAsync();
        firstInstall.Success.Should().BeTrue();
        var reinstall = await adapter.InstallPluginAsync();
        reinstall.Success.Should().BeTrue();
        reinstall.BackupPath.Should().NotBeNullOrWhiteSpace();
        reinstall.BackupPath!.EndsWith(".bak", StringComparison.OrdinalIgnoreCase).Should().BeTrue();

        var updates = new ConcurrentQueue<AgentSessionSnapshot>();
        adapter.SessionUpdated += (_, snap) => updates.Enqueue(snap);

        await adapter.StartAsync();

        // Give HTTP loop a moment to receive 401 Unauthorized first, then verify named pipe still accepts v2 events
        await Task.Delay(60);

        async Task SendPipeLineAsync(string jsonLine)
        {
            await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(3000);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(jsonLine);
        }

        await SendPipeLineAsync("""{"kind":"handshake","supportsInteractivePermissionHook":false}""");
        await WaitUntilAsync(() => adapter.IsPluginBridgeActive && adapter.ConnectionState == AgentConnectionState.Connected);

        // Health check hitting HTTP 401 must keep plugin bridge Healthy and Connected (never disconnect plugin sessions)
        var healthWhile401 = await adapter.CheckHealthAsync();
        healthWhile401.Status.Should().Be(AdapterHealthStatus.Healthy);
        healthWhile401.Message.Should().Contain("Plugin bridge active");
        adapter.ConnectionState.Should().Be(AgentConnectionState.Connected);

        // 1. OpenCode v2 session.created uses "data" envelope + nested location.directory
        await SendPipeLineAsync("""{"id":"evt_1","type":"session.created","created":1763820000000,"data":{"sessionID":"ses_v2","location":{"directory":"C:\\repos\\shima"},"title":"Fix island bridge"}}""");
        await WaitUntilAsync(() => updates.Any(s => s.SessionId == "opencode:ses_v2" && s.TaskSummary == "Fix island bridge"));
        updates.Last().Should().Match<AgentSessionSnapshot>(s =>
            s.SessionId == "opencode:ses_v2"
            && s.WorkspaceName == "shima"
            && s.WorkspacePath == @"C:\repos\shima"
            && s.Status == AgentStatus.Working
            && s.ConnectionState == AgentConnectionState.Connected);

        // 2. High-frequency deltas & global catalog events are quietly skipped (never counted as ambiguous)
        await SendPipeLineAsync("""{"id":"evt_delta","type":"session.text.delta","created":1763820001000,"data":{"sessionID":"ses_v2","delta":"hi"}}""");
        await SendPipeLineAsync("""{"id":"evt_model","type":"model.updated","created":1763820001100,"data":{}}""");

        // 3. OpenCode v2 tool start -> Working with tool name
        await SendPipeLineAsync("""{"id":"evt_2","type":"session.tool.input.started","created":1763820002000,"data":{"sessionID":"ses_v2","assistantMessageID":"msg_1","id":"call_1","name":"shell"}}""");
        await WaitUntilAsync(() => updates.Any(s => s.SessionId == "opencode:ses_v2" && s.TaskSummary == "Running shell"));
        adapter.AmbiguousEventsIgnoredCount.Should().Be(0);

        // 4. OpenCode v2 permission.asked uses action + resources array
        await SendPipeLineAsync("""{"id":"evt_3","type":"permission.asked","created":1763820003000,"data":{"id":"per_1","sessionID":"ses_v2","action":"shell","resources":["dotnet test"]}}""");
        await WaitUntilAsync(() => updates.Any(s => s.SessionId == "opencode:ses_v2" && s.Status == AgentStatus.AwaitingApproval));
        updates.Last().Should().Match<AgentSessionSnapshot>(s =>
            s.Status == AgentStatus.AwaitingApproval
            && s.PendingRequestId == "per_1"
            && s.PendingToolName == "shell"
            && s.PendingActionSummary == "dotnet test");

        // 5. OpenCode v2 permission.replied clears approval -> Working
        await SendPipeLineAsync("""{"id":"evt_4","type":"permission.replied","created":1763820004000,"data":{"sessionID":"ses_v2","requestID":"per_1","reply":"once"}}""");
        await WaitUntilAsync(() => updates.Last().Status == AgentStatus.Working && updates.Last().PendingRequestId == null);

        // 6. OpenCode v2 session.execution.succeeded -> Finished
        await SendPipeLineAsync("""{"id":"evt_5","type":"session.execution.succeeded","created":1763820005000,"data":{"sessionID":"ses_v2"}}""");
        await WaitUntilAsync(() => updates.Last().Status == AgentStatus.Finished);

        // 7. Duplicate of evt_5 from a second plugin supervisor is deduplicated; new turn evt_6 transitions back to Working
        await SendPipeLineAsync("""{"id":"evt_5","type":"session.execution.succeeded","created":1763820005000,"data":{"sessionID":"ses_v2"}}""");
        await SendPipeLineAsync("""{"id":"evt_6","type":"session.execution.started","created":1763820006000,"directory":"C:\\repos\\shima","sessionTitle":"Second turn","data":{"sessionID":"ses_v2"}}""");
        await WaitUntilAsync(() => updates.Last().Status == AgentStatus.Working && updates.Last().TaskSummary == "Second turn");
        adapter.DuplicateEventsIgnoredCount.Should().BeGreaterThanOrEqualTo(1);

        // 8. OpenCode v2 session.execution.failed -> NeedsAttention (fatal) with error message
        await SendPipeLineAsync("""{"id":"evt_7","type":"session.execution.failed","created":1763820007000,"data":{"sessionID":"ses_v2","error":{"type":"api","message":"Provider rate limit"}}}""");
        await WaitUntilAsync(() => updates.Last().Status == AgentStatus.NeedsAttention && updates.Last().TaskSummary == "Provider rate limit");
        updates.Last().IsFatalError.Should().BeTrue();

        // 9. Repeated HTTP 401 health check must NEVER override tracked named-pipe sessions to Disconnected
        var postEventsHealth = await adapter.CheckHealthAsync();
        postEventsHealth.Status.Should().Be(AdapterHealthStatus.Healthy);
        adapter.ConnectionState.Should().Be(AgentConnectionState.Connected);
        updates.Last().ConnectionState.Should().Be(AgentConnectionState.Connected);
    }

    [Fact]
    public async Task DeveloperWidgetModule_CoversAll5BarStates_Coexistence_DwellRetention_AutoOpenAndWpfViews()
    {
        var now = new DateTimeOffset(2026, 11, 22, 15, 0, 0, TimeSpan.Zero);
        var prefs = new FakeAppPreferencesService(new AppPreferencesSnapshot
        {
            ClaudeCodeOptInEnabled = false,
            OpenCodeOptInEnabled = false
        });
        var fakeClaude = new FakeCodingAgentAdapter("claude-code", "Claude Code", supportsApproval: true, supportsFocus: true, prefs);
        var fakeOpenCode = new FakeCodingAgentAdapter("opencode", "OpenCode", supportsApproval: true, supportsFocus: false, prefs);
        var repo = Substitute.For<IAgentSessionLogRepository>();
        var pinController = new OverlayPinController();
        using var stateManager = new IslandStateManager(pinController);
        var events = new InMemoryEventDispatcher();
        var requester = new IslandAttentionRequester(stateManager);

        var barVisibilityEvents = new List<CollapsedStatusIndicatorChangedEvent>();
        using var barSub = events.Subscribe<CollapsedStatusIndicatorChangedEvent>(e => barVisibilityEvents.Add(e));
        var peeks = new List<NotificationPeekContentEvent>();
        using var peekSub = events.Subscribe<NotificationPeekContentEvent>(e => peeks.Add(e));

        var dwellTimer = new FakeOneShotTimer();
        var errorTimer = new FakeOneShotTimer();
        var retentionTimer = new FakeOneShotTimer();

        await using var module = new DeveloperWidgetModule(
            [fakeClaude, fakeOpenCode],
            repo,
            prefs,
            dwellTimer,
            errorTimer,
            retentionTimer,
            utcNowProvider: () => now);

        var context = CreateWidgetContext(events, requester);
        await module.InitializeAsync(context, CancellationToken.None);

        // 1. Disabled/Unconfigured -> Hidden bar (no collapsed status view), idle empty state "No active sessions"
        module.ViewModel.BarVisualState.Should().Be(DeveloperBarVisualState.Hidden);
        module.ViewModel.IsCollapsedBarVisible.Should().BeFalse();
        module.ViewModel.BarFillPercent.Should().Be(0.0);
        module.CreateCollapsedStatusView().Should().BeNull();
        module.ViewModel.IsEmpty.Should().BeTrue();
        module.ViewModel.EmptyStateLabel.Should().Be("No active sessions");
        module.ShouldAutoSelectOnExpand.Should().BeFalse();

        // 2. Opt into OpenCode with no running activity -> Idle state keeps collapsed bar hidden (no idle gray bar)
        await prefs.SaveAsync(new AppPreferencesSnapshot { OpenCodeOptInEnabled = true });
        module.ViewModel.BarVisualState.Should().Be(DeveloperBarVisualState.Idle);
        module.ViewModel.IsCollapsedBarVisible.Should().BeFalse();
        module.ViewModel.BarFillPercent.Should().Be(0.0);
        module.CreateCollapsedStatusView().Should().BeNull();
        module.ViewModel.BarFillBrushKey.Should().Be("AgentStatusIdleBrush");
        module.ShouldAutoSelectOnExpand.Should().BeFalse();
        barVisibilityEvents.Should().NotContain(e => e.IsVisible);

        // 3. Working alongside Now Playing or Focus Timer -> yields compact pill priority while showing green Working bar
        requester.UpdateCompactPill("shima.music", "Spotify · Song");
        requester.UpdateCompactPill("shima.focus", "Focus 24:00");
        stateManager.CompactActiveWidgetId.Should().Be("shima.focus");

        fakeOpenCode.EmitSession(new AgentSessionSnapshot(
            "opencode:s1", "opencode", "shima", @"C:\repos\shima", null,
            AgentStatus.Working, false, AgentConnectionState.Connected,
            "Implement parser", null, null, null, false, now, now));

        module.ViewModel.BarVisualState.Should().Be(DeveloperBarVisualState.Working);
        module.ViewModel.IsCollapsedBarVisible.Should().BeTrue();
        module.ViewModel.BarFillBrushKey.Should().Be("AgentStatusWorkingBrush");
        module.ViewModel.BarFillPercent.Should().BeLessThan(100.0);
        barVisibilityEvents.Should().Contain(e => e.IsVisible);
        module.ShouldAutoSelectOnExpand.Should().BeTrue();
        stateManager.CompactActiveWidgetId.Should().Be("shima.focus"); // Yielded to active Focus Timer!
        requester.UpdateCompactPill("shima.focus", null);
        stateManager.CompactActiveWidgetId.Should().Be("shima.music"); // Yielded to Now Playing!

        // 4. Actionable AwaitingApproval takes Urgent compact-pill precedence & emits peek with Allow/Deny
        fakeClaude.EmitSession(new AgentSessionSnapshot(
            "claude-code:c1", "claude-code", "api", @"C:\repos\api", 3322,
            AgentStatus.AwaitingApproval, false, AgentConnectionState.Connected,
            "Run migration", "Bash", "dotnet ef database update", "req_1", true, now, now));

        module.ViewModel.BarVisualState.Should().Be(DeveloperBarVisualState.Attention);
        module.ViewModel.BarFillBrushKey.Should().Be("AgentStatusAttentionBrush");
        stateManager.CompactActiveWidgetId.Should().Be(DeveloperWidgetModule.Id);
        peeks.Should().ContainSingle(p => p.PrimaryAction != null && p.SecondaryAction != null);

        var claudeRow = module.ViewModel.Sessions.Single(s => s.SessionId == "claude-code:c1");
        claudeRow.ShowApprovalButtons.Should().BeTrue();
        claudeRow.CanFocusTerminal.Should().BeTrue();

        // Failed confirmation keeps Allow/Deny visible and surfaces failure feedback without optimistic commit
        fakeClaude.NextApprovalResult = new PermissionActionResult(false, null, "Server 500");
        var failedResult = await module.ViewModel.ApproveSessionAsync("claude-code:c1", "req_1");
        failedResult.Confirmed.Should().BeFalse();
        claudeRow.ShowApprovalButtons.Should().BeTrue();
        claudeRow.DetailText.Should().Be("Server 500");

        // Approve request -> confirmed update clears urgent compact pill so Now Playing resumes!
        fakeClaude.NextApprovalResult = new PermissionActionResult(true, "claude.pipe.ack", null);
        var approveResult = await module.ViewModel.ApproveSessionAsync("claude-code:c1", "req_1");
        approveResult.Confirmed.Should().BeTrue();
        stateManager.CompactActiveWidgetId.Should().Be("shima.music");

        // Non-approval row click -> FocusTerminal
        (await module.ViewModel.FocusSessionTerminalAsync("claude-code:c1")).Should().BeTrue();
        fakeClaude.FocusedSessionIds.Should().Contain("claude-code:c1");
        // Headless OpenCode row cannot focus terminal
        module.ViewModel.Sessions.Single(s => s.SessionId == "opencode:s1").CanFocusTerminal.Should().BeFalse();
        (await module.ViewModel.FocusSessionTerminalAsync("opencode:s1")).Should().BeFalse();

        // 5. All sessions finish -> 10s Finished bar dwell + 15m expanded retention
        now = now.AddMinutes(2);
        fakeOpenCode.EmitSession(new AgentSessionSnapshot(
            "opencode:s1", "opencode", "shima", @"C:\repos\shima", null,
            AgentStatus.Finished, false, AgentConnectionState.Connected,
            "Implement parser", null, null, null, false, now.AddMinutes(-2), now));
        fakeClaude.EmitSession(new AgentSessionSnapshot(
            "claude-code:c1", "claude-code", "api", @"C:\repos\api", 3322,
            AgentStatus.Finished, false, AgentConnectionState.Connected,
            "Run migration", null, null, null, false, now.AddMinutes(-2), now));

        module.ViewModel.BarVisualState.Should().Be(DeveloperBarVisualState.Finished);
        module.ViewModel.BarFillBrushKey.Should().Be("AgentStatusFinishedBrush");
        dwellTimer.IsPending.Should().BeTrue();
        dwellTimer.LastDelay.Should().Be(DeveloperWidgetViewModel.FinishedBarDwellDuration);

        // Expire 10s dwell -> collapsed bar hides again (Idle), finished rows stay 15m in expanded list
        dwellTimer.Fire();
        module.ViewModel.BarVisualState.Should().Be(DeveloperBarVisualState.Idle);
        module.ViewModel.IsCollapsedBarVisible.Should().BeFalse();
        barVisibilityEvents[^1].IsVisible.Should().BeFalse();
        module.ViewModel.Sessions.Should().HaveCount(2);

        // 6. Fatal Error/Failed holds ErrorFailed bar until hovered or 15m cap; DisplayOff suspends all timers
        fakeOpenCode.EmitSession(new AgentSessionSnapshot(
            "opencode:err", "opencode", "shima", @"C:\repos\shima", null,
            AgentStatus.NeedsAttention, IsFatalError: true, AgentConnectionState.Connected,
            "Crash", null, null, null, false, now, now));

        module.ViewModel.BarVisualState.Should().Be(DeveloperBarVisualState.ErrorFailed);
        module.ViewModel.BarFillBrushKey.Should().Be("AgentStatusErrorBrush");
        errorTimer.IsPending.Should().BeTrue();
        errorTimer.LastDelay.Should().Be(DeveloperWidgetViewModel.ErrorBarMaxDwellDuration);

        // DisplayOff suspends timers cleanly (0% idle CPU)
        module.OnPowerProfileChanged(PowerProfile.DisplayOff);
        module.ViewModel.HasPendingTimers.Should().BeFalse();
        module.OnPowerProfileChanged(PowerProfile.AC);

        // Hover collapsed bar acknowledges ErrorFailed -> hides bar while retaining row for 15m
        module.OnCollapsedNotchHovered();
        module.ViewModel.BarVisualState.Should().Be(DeveloperBarVisualState.Idle);
        module.ViewModel.IsCollapsedBarVisible.Should().BeFalse();
        barVisibilityEvents[^1].IsVisible.Should().BeFalse();
        module.ViewModel.Sessions.Should().Contain(s => s.SessionId == "opencode:err");

        // After 15m retention expiry -> rows purge leaving "No active sessions"
        module.ViewModel.PurgeExpiredRowsForTesting(now.AddMinutes(16));
        module.ViewModel.IsEmpty.Should().BeTrue();
        module.CreateCollapsedStatusView().Should().BeNull();

        // 7. STA WPF View instantiation check (emit active session so collapsed bar view is active, then verify Idle collapses it)
        fakeOpenCode.EmitSession(new AgentSessionSnapshot(
            "opencode:s2", "opencode", "shima", @"C:\repos\shima", null,
            AgentStatus.Working, false, AgentConnectionState.Connected,
            "Active build", null, null, null, false, now.AddMinutes(17), now.AddMinutes(17)));

        Exception? staException = null;
        var done = new ManualResetEventSlim(false);
        var staThread = new Thread(() =>
        {
            try
            {
                var collapsedBar = module.CreateCollapsedStatusView().Should().BeOfType<DeveloperCollapsedBarView>().Subject;
                collapsedBar.Measure(new Size(100, 12));
                collapsedBar.Arrange(new Rect(0, 0, 100, 12));
                collapsedBar.Visibility.Should().Be(Visibility.Visible);

                var compactView = module.CreateCompactActiveView().Should().BeOfType<DeveloperCompactView>().Subject;
                compactView.Measure(new Size(220, 32));
                compactView.Arrange(new Rect(0, 0, 220, 32));

                var expanded = module.CreateExpandedView().Should().BeOfType<DeveloperView>().Subject;
                expanded.Measure(new Size(360, 140));
                expanded.Arrange(new Rect(0, 0, 360, 140));

                var settings = module.CreateSettingsView().Should().BeOfType<DeveloperSettingsView>().Subject;
                settings.Measure(new Size(600, 420));
                settings.Arrange(new Rect(0, 0, 600, 420));
            }
            catch (Exception ex)
            {
                staException = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
                done.Set();
            }
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        done.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
        staException.Should().BeNull();
    }

    private static IWidgetContext CreateWidgetContext(IEventDispatcher events, IIslandAttentionRequester island)
    {
        var settings = Substitute.For<IWidgetSettingsStore>();
        settings.ReadJsonAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>("{}"));

        var ctx = Substitute.For<IWidgetContext>();
        ctx.WidgetId.Returns(DeveloperWidgetModule.Id);
        ctx.Events.Returns(events);
        ctx.Settings.Returns(settings);
        ctx.Island.Returns(island);
        ctx.Theme.Returns(Substitute.For<IThemeTokenProvider>());
        ctx.Logger.Returns(Substitute.For<ILogger>());
        return ctx;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 4000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(15);
        }

        condition().Should().BeTrue();
    }

    private static HttpResponseMessage JsonOk(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class FakeAppPreferencesService(AppPreferencesSnapshot initial) : IAppPreferencesService
    {
        private string? _password;

        public AppPreferencesSnapshot Current { get; private set; } = initial;

        public event EventHandler<AppPreferencesSnapshot>? PreferencesChanged;

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SaveAsync(
            AppPreferencesSnapshot snapshot,
            string? newOpenCodeServerPassword = null,
            bool clearOpenCodeServerPassword = false,
            string? newLicenseKey = null,
            bool clearLicenseKey = false,
            CancellationToken ct = default)
        {
            if (clearOpenCodeServerPassword)
            {
                _password = null;
            }
            else if (newOpenCodeServerPassword is not null)
            {
                _password = newOpenCodeServerPassword;
            }

            Current = snapshot with { HasOpenCodeServerPassword = !string.IsNullOrEmpty(_password) };
            PreferencesChanged?.Invoke(this, Current);
            return Task.CompletedTask;
        }

        public string? ReadOpenCodeServerPassword() => _password;

        public Task ResetToDefaultsAsync(CancellationToken ct = default)
        {
            _password = null;
            Current = new AppPreferencesSnapshot();
            PreferencesChanged?.Invoke(this, Current);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTerminalWindowFocuser : ITerminalWindowFocuser
    {
        public bool IsSupported => true;
        public bool FocusResult { get; set; } = true;
        public int? LastFocusedPid { get; private set; }

        public int? ResolveHostProcessId(int processId) => processId;

        public bool TryFocusProcessWindow(int processId)
        {
            LastFocusedPid = processId;
            return FocusResult;
        }
    }

    private sealed class ScriptedHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        public ScriptedHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> syncResponder)
            : this(req => Task.FromResult(syncResponder(req)))
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            responder(request);
    }

    private sealed class KeepOpenSseContent(string initialChunk) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(initialChunk);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new KeepOpenMemoryStream(Encoding.UTF8.GetBytes(initialChunk)));

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }

    private sealed class KeepOpenMemoryStream(byte[] initialBytes) : Stream
    {
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _offset;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset < initialBytes.Length)
            {
                var count = Math.Min(buffer.Length, initialBytes.Length - _offset);
                initialBytes.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
                return count;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FakeCodingAgentAdapter(
        string adapterId,
        string displayName,
        bool supportsApproval,
        bool supportsFocus,
        IAppPreferencesService prefs) : ICodingAgentAdapter
    {
        private readonly Dictionary<string, AgentSessionSnapshot> _sessions = new(StringComparer.Ordinal);

        public string AdapterId => adapterId;
        public string DisplayName => displayName;
        public AgentConnectionState ConnectionState { get; private set; } = AgentConnectionState.Connected;
        public PermissionActionResult NextApprovalResult { get; set; } = new(true, "fake.ack", null);
        public List<string> FocusedSessionIds { get; } = [];

        public AgentCapabilities Capabilities => new(
            supportsApproval,
            true,
            supportsFocus,
            true,
            adapterId == "claude-code" ? prefs.Current.ClaudeCodeOptInEnabled : prefs.Current.OpenCodeOptInEnabled,
            Array.Empty<string>());

        public event EventHandler<AgentSessionSnapshot>? SessionUpdated;
        public event EventHandler<string>? SessionRemoved;
        public event EventHandler<AgentConnectionState>? ConnectionStateChanged;

        public void EmitSession(AgentSessionSnapshot snap)
        {
            _sessions[snap.SessionId] = snap;
            SessionUpdated?.Invoke(this, snap);
        }

        public void RemoveSession(string sessionId)
        {
            if (_sessions.Remove(sessionId))
            {
                SessionRemoved?.Invoke(this, sessionId);
            }
        }

        public Task<AdapterHealthResult> CheckHealthAsync(CancellationToken ct = default) =>
            Task.FromResult(new AdapterHealthResult(AdapterHealthStatus.Healthy, "Healthy"));

        public Task StartAsync(CancellationToken ct = default)
        {
            ConnectionState = AgentConnectionState.Connected;
            ConnectionStateChanged?.Invoke(this, ConnectionState);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct = default)
        {
            ConnectionState = AgentConnectionState.Disconnected;
            ConnectionStateChanged?.Invoke(this, ConnectionState);
            return Task.CompletedTask;
        }

        public Task<PermissionActionResult> RespondToApprovalAsync(
            string sessionId,
            string requestId,
            bool allow,
            string? reason,
            CancellationToken ct = default)
        {
            if (NextApprovalResult.Confirmed && _sessions.TryGetValue(sessionId, out var current))
            {
                var updated = current with
                {
                    Status = AgentStatus.Working,
                    PendingRequestId = null,
                    PendingToolName = null,
                    PendingActionSummary = null,
                    HasActionablePermissionRequest = false
                };
                _sessions[sessionId] = updated;
                SessionUpdated?.Invoke(this, updated);
            }

            return Task.FromResult(NextApprovalResult);
        }

        public Task<bool> FocusTerminalAsync(string sessionId, CancellationToken ct = default)
        {
            if (!supportsFocus)
            {
                return Task.FromResult(false);
            }

            FocusedSessionIds.Add(sessionId);
            return Task.FromResult(true);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeOneShotTimer : IOneShotTimer
    {
        private Action? _callback;

        public bool IsPending { get; private set; }
        public TimeSpan? LastDelay { get; private set; }

        public void Schedule(TimeSpan delay, Action callback)
        {
            LastDelay = delay;
            _callback = callback;
            IsPending = true;
        }

        public void Fire()
        {
            if (!IsPending)
            {
                return;
            }

            IsPending = false;
            var cb = _callback;
            _callback = null;
            cb?.Invoke();
        }

        public void Cancel()
        {
            IsPending = false;
            _callback = null;
        }

        public void Dispose() => Cancel();
    }
}
