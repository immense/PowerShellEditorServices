// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Management.Automation;
using System.Management.Automation.Language;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.PowerShell.EditorServices.Services;
using Microsoft.PowerShell.EditorServices.Services.DebugAdapter;
using Microsoft.PowerShell.EditorServices.Services.PowerShell;
using Microsoft.PowerShell.EditorServices.Services.PowerShell.Debugging;
using Microsoft.PowerShell.EditorServices.Services.PowerShell.Execution;
using Microsoft.PowerShell.EditorServices.Services.PowerShell.Runspace;
using Microsoft.PowerShell.EditorServices.Services.TextDocument;
using Microsoft.PowerShell.EditorServices.Utility;
using OmniSharp.Extensions.DebugAdapter.Protocol.Events;
using OmniSharp.Extensions.DebugAdapter.Protocol.Requests;
using OmniSharp.Extensions.DebugAdapter.Protocol.Server;

namespace Microsoft.PowerShell.EditorServices.Handlers
{
    internal class ConfigurationDoneHandler : IConfigurationDoneHandler
    {
        // TODO: We currently set `WriteInputToHost` as true, which writes our debugged commands'
        // `GetInvocationText` and that reveals some obscure implementation details we should
        // instead hide from the user with pretty strings (or perhaps not write out at all).
        //
        // This API is mostly used for F5 execution so it requires the foreground.
        private static readonly PowerShellExecutionOptions s_debuggerExecutionOptions = new()
        {
            RequiresForeground = true,
            WriteInputToHost = true,
            WriteOutputToHost = true,
            ThrowOnError = false,
            AddToHistory = true,
        };

        private readonly ILogger _logger;
        private readonly IDebugAdapterServerFacade _debugAdapterServer;
        private readonly DebugService _debugService;
        private readonly DebugStateService _debugStateService;
        private readonly DebugEventHandlerService _debugEventHandlerService;
        private readonly IInternalPowerShellExecutionService _executionService;
        private readonly WorkspaceService _workspaceService;
        private readonly IPowerShellDebugContext _debugContext;
        private readonly IRunspaceContext _runspaceContext;

        // TODO: Decrease these arguments since they're a bunch of interfaces that can be simplified
        // (i.e., `IRunspaceContext` should just be available on `IPowerShellExecutionService`).
        public ConfigurationDoneHandler(
            ILoggerFactory loggerFactory,
            IDebugAdapterServerFacade debugAdapterServer,
            DebugService debugService,
            DebugStateService debugStateService,
            DebugEventHandlerService debugEventHandlerService,
            IInternalPowerShellExecutionService executionService,
            WorkspaceService workspaceService,
            IPowerShellDebugContext debugContext,
            IRunspaceContext runspaceContext)
        {
            _logger = loggerFactory.CreateLogger<ConfigurationDoneHandler>();
            _debugAdapterServer = debugAdapterServer;
            _debugService = debugService;
            _debugStateService = debugStateService;
            _debugEventHandlerService = debugEventHandlerService;
            _executionService = executionService;
            _workspaceService = workspaceService;
            _debugContext = debugContext;
            _runspaceContext = runspaceContext;
        }

        public Task<ConfigurationDoneResponse> Handle(ConfigurationDoneArguments request, CancellationToken cancellationToken)
        {
            System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] ConfigurationDoneHandler.Handle called, ScriptToLaunch='{_debugStateService?.ScriptToLaunch}'\n");
            _debugService.IsClientAttached = true;

            if (!string.IsNullOrEmpty(_debugStateService.ScriptToLaunch))
            {
                // NOTE: This is an unawaited task because responding to "configuration done" means
                // setting up the debugger, and in our case that means starting the script but not
                // waiting for it to finish.
                Task _ = LaunchScriptAsync(_debugStateService.ScriptToLaunch).HandleErrorsAsync(_logger);
            }

            if (_debugStateService.IsInteractiveDebugSession && _debugService.IsDebuggerStopped)
            {
                if (_debugService.CurrentDebuggerStoppedEventArgs is not null)
                {
                    // If this is an interactive session and there's a pending breakpoint, send that
                    // information along to the debugger client.
                    _debugEventHandlerService.TriggerDebuggerStopped(_debugService.CurrentDebuggerStoppedEventArgs);
                }
                else
                {
                    // If this is an interactive session and there's a pending breakpoint that has
                    // not been propagated through the debug service, fire the debug service's
                    // OnDebuggerStop event.
                    _debugService.OnDebuggerStopAsync(null, _debugContext.LastStopEventArgs);
                }
            }

            return Task.FromResult(new ConfigurationDoneResponse());
        }

        // NOTE: We test this function in `DebugServiceTests` so it both needs to be internal, and
        // use conditional-access on `_debugStateService` and `_debugAdapterServer` as its not set
        // by tests.
        internal async Task LaunchScriptAsync(string scriptToLaunch)
        {
            System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] LaunchScriptAsync: scriptToLaunch='{scriptToLaunch}'\n");
            PSCommand command;
            if (System.IO.File.Exists(scriptToLaunch))
            {
                System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] LaunchScriptAsync: File.Exists=true\n");
                // For a saved file we just execute its path (after escaping it).
                command = PSCommandHelpers.BuildDotSourceCommandWithArguments(
                    PSCommandHelpers.EscapeScriptFilePath(scriptToLaunch), _debugStateService?.Arguments);
            }
            else // It's a URI to an untitled script, or a raw script.
            {
                bool isScriptFile = _workspaceService.TryGetFile(scriptToLaunch, out ScriptFile untitledScript);
                System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] LaunchScriptAsync: TryGetFile={isScriptFile}, SupportsBreakpointApis={isScriptFile && BreakpointApiUtils.SupportsBreakpointApis(_runspaceContext.CurrentRunspace)}\n");
                if (isScriptFile)
                {
                    System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] LaunchScriptAsync: contents.Length={untitledScript.Contents.Length}, DocumentUri={untitledScript.DocumentUri}\n");
                }
                if (isScriptFile && BreakpointApiUtils.SupportsBreakpointApis(_runspaceContext.CurrentRunspace))
                {
                    // Use the DocumentUri directly — the frontend now uses pspath:// URIs everywhere.
                    string scriptUri = untitledScript.DocumentUri.ToString();

                    ScriptBlockAst ast = Parser.ParseInput(
                        untitledScript.Contents,
                        scriptUri,
                        out Token[] _,
                        out ParseError[] _);

                    command = PSCommandHelpers
                        .BuildDotSourceCommandWithArguments("$args[0]", _debugStateService?.Arguments)
                        .AddArgument(ast.GetScriptBlock());
                }
                else
                {
                    System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] LaunchScriptAsync: FALLBACK inline execution (isScriptFile={isScriptFile})\n");
                    // Without the new APIs we can only execute the untitled script's contents.
                    // Command breakpoints and `Wait-Debugger` will work. We must wrap the script
                    // with newlines so that any included comments don't break the command.
                    command = PSCommandHelpers.BuildDotSourceCommandWithArguments(
                        string.Concat(
                            "{" + System.Environment.NewLine,
                            isScriptFile ? untitledScript.Contents : scriptToLaunch,
                            System.Environment.NewLine + "}"),
                            _debugStateService?.Arguments);
                }
            }

            // Check debugger state before execution
            var debugger = _runspaceContext.CurrentRunspace.Runspace.Debugger;
            var hostRunspace = ((Microsoft.PowerShell.EditorServices.Services.PowerShell.Host.PsesInternalHost)_executionService).Runspace;
            System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] LaunchScriptAsync: runspaceContext.Runspace.Id={_runspaceContext.CurrentRunspace.Runspace.Id}, hostRunspace.Id={hostRunspace.Id}, same={_runspaceContext.CurrentRunspace.Runspace == hostRunspace}\n");
            System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] LaunchScriptAsync: debugger DebugMode={debugger.DebugMode}, IsActive={debugger.IsActive}\n");
            
            // Check _debuggingMode via reflection
            try {
                var debuggerType = debugger.GetType();
                var contextField = debuggerType.GetField("_context", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (contextField != null) {
                    var context = contextField.GetValue(debugger);
                    var execContextType = context.GetType();
                    var debuggingModeField = execContextType.GetField("_debuggingMode", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (debuggingModeField != null) {
                        var debuggingMode = debuggingModeField.GetValue(context);
                        System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] LaunchScriptAsync: _context._debuggingMode={debuggingMode}\n");
                    }
                }
            } catch (System.Exception ex) {
                System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] LaunchScriptAsync: reflection failed: {ex.Message}\n");
            }
            var breakpoints = BreakpointApiUtils.GetBreakpoints(debugger, _debugStateService.RunspaceId);
            System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] LaunchScriptAsync: RunspaceId={_debugStateService.RunspaceId}, {breakpoints.Count} breakpoints registered\n");
            foreach (var bp in breakpoints)
            {
                if (bp is System.Management.Automation.LineBreakpoint lbp)
                    System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES]   LineBreakpoint: script='{lbp.Script}', line={lbp.Line}, column={lbp.Column}\n");
                else
                    System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES]   {bp.GetType().Name}: {bp}\n");
            }

            // Check the pending breakpoints dictionary by looking at the debugger's internal state
            // We can use GetBreakpoints to see all breakpoints

            System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] LaunchScriptAsync: executing command on thread {System.Environment.CurrentManagedThreadId}\n");

            // Add a DIRECT DebuggerStop handler to see if the event is raised at all
            System.EventHandler<System.Management.Automation.DebuggerStopEventArgs> directHandler = null;
            try {
            directHandler = (s, e) =>
            {
                try { System.Console.Error.WriteLine("PSES_DIRECT_DEBUGGERSTOP_CALLED"); System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] DIRECT DebuggerStop handler called! Breakpoints={e.Breakpoints.Count}, Thread={System.Environment.CurrentManagedThreadId}\n"); } catch (System.Exception ex) { System.Console.Error.WriteLine($"PSES_DIRECT_FAILED: {ex.Message}"); }
            };
            var dbgForHandler = _runspaceContext.CurrentRunspace.Runspace.Debugger;
            System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] About to subscribe DebuggerStop, debugger={dbgForHandler?.GetType().Name}, hash={dbgForHandler?.GetHashCode()}\n");
            dbgForHandler.DebuggerStop += directHandler;
            System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] DIRECT DebuggerStop handler subscribed, debugger hash={dbgForHandler.GetHashCode()}\n");
            } catch (System.Exception ex) {
                System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] Failed to subscribe DebuggerStop: {ex.GetType().Name}: {ex.Message}\n");
            }

            // Fix: Ensure _context.CurrentRunspace is set on the debugger's ExecutionContext
            // In PSES with UseCurrentThread, the debugger's _context.CurrentRunspace can be null
            // which causes OnDebuggerStop to crash with NullReferenceException
            try {
                var dbgFix = _runspaceContext.CurrentRunspace.Runspace.Debugger;
                var dbgTypeFix = dbgFix.GetType();
                var contextFieldFix = dbgTypeFix.GetField("_context", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] CurrentRunspace fix: contextField={contextFieldFix != null}\n");
                if (contextFieldFix != null) {
                    var contextFix = contextFieldFix.GetValue(dbgFix);
                    var currentRunspacePropFix = contextFix?.GetType().GetProperty("CurrentRunspace", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (currentRunspacePropFix != null) {
                        var currentRunspaceFix = currentRunspacePropFix.GetValue(contextFix);
                        System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] CurrentRunspace fix: currentRunspace={currentRunspaceFix?.GetHashCode()}, isNull={currentRunspaceFix == null}\n");
                        if (currentRunspaceFix == null) {
                            System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] Fixing null CurrentRunspace on debugger._context\n");
                            currentRunspacePropFix.SetValue(contextFix, _runspaceContext.CurrentRunspace.Runspace);
                        }
                    }
                }
            } catch (System.Exception ex) {
                System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] Failed to fix CurrentRunspace: {ex.Message}\n");
            }

            // Also check _mapScriptToBreakpoints to see if the script is registered
            try {
                var dbg = _runspaceContext.CurrentRunspace.Runspace.Debugger;
                var dbgType = dbg.GetType();
                // Check LanguageMode
                var langMode = _runspaceContext.CurrentRunspace.Runspace.SessionStateProxy.LanguageMode;
                System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] LanguageMode={langMode}\n");
                // Check if IgnoreScriptDebug is set
                var contextField = dbgType.GetField("_context", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (contextField != null) {
                    var context = contextField.GetValue(dbg);
                    var execContextType = context.GetType();
                    var ignoreScriptDebugField = execContextType.GetProperty("IgnoreScriptDebug", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (ignoreScriptDebugField != null) {
                        var ignoreScriptDebug = ignoreScriptDebugField.GetValue(context);
                        System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] IgnoreScriptDebug={ignoreScriptDebug}\n");
                    }
                    // Check _debuggingMode from context
                    var debuggingModeField = execContextType.GetField("_debuggingMode", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (debuggingModeField != null) {
                        var debuggingMode = debuggingModeField.GetValue(context);
                        System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] _context._debuggingMode={debuggingMode}\n");
                    }
                    // Check if ExecutionContext from TLS matches
                    var localPipelineType = typeof(System.Management.Automation.Runspaces.Runspace).Assembly.GetType("System.Management.Automation.Runspaces.LocalPipeline");
                    var getCtxMethod = localPipelineType?.GetMethod("GetExecutionContextFromTLS", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                    var tlsContext = getCtxMethod?.Invoke(null, null);
                    System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] TLS context == debugger context: {tlsContext == context}\n");
                    if (tlsContext != null) {
                        var tlsDebugMode = execContextType.GetField("_debuggingMode", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(tlsContext);
                        System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] TLS context _debuggingMode={tlsDebugMode}\n");
                    }
                }
                var mapField = dbgType.GetField("_mapScriptToBreakpoints", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (mapField != null) {
                    var map = mapField.GetValue(dbg) as System.Collections.IDictionary;
                    System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] _mapScriptToBreakpoints count={map?.Count}\n");
                    if (map != null) {
                        foreach (System.Collections.DictionaryEntry entry in map) {
                            System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES]   map key type={entry.Key?.GetType().Name}, key={entry.Key?.ToString()?.Substring(0, System.Math.Min(80, entry.Key?.ToString()?.Length ?? 0))}\n");
                        }
                    }
                }
            } catch (System.Exception ex) {
                System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] map reflection failed: {ex.Message}\n");
            }

            // Fix: Ensure _context.CurrentRunspace is set on the debugger's ExecutionContext
            // In PSES with UseCurrentThread, the debugger's _context.CurrentRunspace can be null
            // which causes OnDebuggerStop to crash with NullReferenceException
            try {
                var dbg = _runspaceContext.CurrentRunspace.Runspace.Debugger;
                var dbgType = dbg.GetType();
                var contextField = dbgType.GetField("_context", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] CurrentRunspace fix: contextField={contextField != null}\n");
                if (contextField != null) {
                    var context = contextField.GetValue(dbg);
                    var currentRunspaceProp = context?.GetType().GetProperty("CurrentRunspace", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] CurrentRunspace fix: currentRunspaceProp={currentRunspaceProp != null}, context={context?.GetHashCode()}\n");
                    if (currentRunspaceProp != null) {
                        var currentRunspace = currentRunspaceProp.GetValue(context);
                        System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] CurrentRunspace fix: currentRunspace={currentRunspace?.GetHashCode()}, isNull={currentRunspace == null}\n");
                        if (currentRunspace == null) {
                            System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] Fixing null CurrentRunspace on debugger._context\n");
                            currentRunspaceProp.SetValue(context, _runspaceContext.CurrentRunspace.Runspace);
                        }
                    }
                }
            } catch (System.Exception ex) {
                System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] Failed to fix CurrentRunspace: {ex.Message}\n");
            }

            // Fix: Set _debuggingMode on the TLS (Thread Local Storage) execution context.
            // The debugger checks _debuggingMode from the TLS context during script execution,
            // not from the runspace's debugger context. If TLS _debuggingMode is 0, the debugger
            // skips breakpoint checks entirely, even though breakpoints are registered.
            try {
                var localPipelineType = typeof(System.Management.Automation.Runspaces.Runspace).Assembly.GetType("System.Management.Automation.Runspaces.LocalPipeline");
                var getCtxMethod = localPipelineType?.GetMethod("GetExecutionContextFromTLS", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                var tlsContext = getCtxMethod?.Invoke(null, null);
                if (tlsContext != null) {
                    var execContextType = tlsContext.GetType();
                    var debuggingModeField = execContextType.GetField("_debuggingMode", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (debuggingModeField != null) {
                        var currentMode = debuggingModeField.GetValue(tlsContext);
                        System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] TLS _debuggingMode before fix: {currentMode}\n");
                        // DebugModes.LocalScript = 1
                        debuggingModeField.SetValue(tlsContext, (System.Management.Automation.DebugModes)1);
                        System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] TLS _debuggingMode set to LocalScript (1)\n");
                    }
                }
            } catch (System.Exception ex) {
                System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] Failed to set TLS _debuggingMode: {ex.Message}\n");
            }

            try {
            await _executionService.ExecutePSCommandAsync(
                command,
                CancellationToken.None,
                s_debuggerExecutionOptions).ConfigureAwait(false);
            } catch (System.Exception ex) {
                System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] LaunchScriptAsync: ExecutePSCommandAsync threw: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n");
            }

            if (directHandler != null)
                _runspaceContext.CurrentRunspace.Runspace.Debugger.DebuggerStop -= directHandler;

            // Check _pendingBreakpoints AFTER execution to see if breakpoint was bound
            try {
                var dbg = _runspaceContext.CurrentRunspace.Runspace.Debugger;
                var dbgType = dbg.GetType();
                var pendingField = dbgType.GetField("_pendingBreakpoints", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (pendingField != null) {
                    var pending = pendingField.GetValue(dbg) as System.Collections.IDictionary;
                    System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] AFTER execution: _pendingBreakpoints count={pending?.Count}\n");
                }
                var idToBpField = dbgType.GetField("_idToBreakpoint", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (idToBpField != null) {
                    var idToBp = idToBpField.GetValue(dbg) as System.Collections.IDictionary;
                    System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] AFTER execution: _idToBreakpoint count={idToBp?.Count}\n");
                }
                // Check _callStack
                var callStackField = dbgType.GetField("_callStack", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (callStackField != null) {
                    var callStack = callStackField.GetValue(dbg);
                    var callStackType = callStack.GetType();
                    var countProp = callStackType.GetProperty("Count");
                    var count = countProp?.GetValue(callStack);
                    System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] AFTER execution: _callStack count={count}\n");
                }
                // Check _mapScriptToBreakpoints
                var mapField2 = dbgType.GetField("_mapScriptToBreakpoints", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (mapField2 != null) {
                    var map = mapField2.GetValue(dbg) as System.Collections.IDictionary;
                    System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] AFTER execution: _mapScriptToBreakpoints count={map?.Count}\n");
                    if (map != null) {
                        foreach (System.Collections.DictionaryEntry entry in map) {
                            System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES]   map key type={entry.Key?.GetType().Name}, key={entry.Key?.ToString()?.Substring(0, System.Math.Min(80, entry.Key?.ToString()?.Length ?? 0))}\n");
                        }
                    }
                }
                // Check _boundBreakpoints
                var boundField = dbgType.GetField("_boundBreakpoints", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (boundField != null) {
                    var bound = boundField.GetValue(dbg) as System.Collections.IDictionary;
                    System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] AFTER execution: _boundBreakpoints count={bound?.Count}\n");
                    if (bound != null) {
                        foreach (System.Collections.DictionaryEntry entry in bound) {
                            System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES]   bound key={entry.Key}\n");
                        }
                    }
                }
            } catch (System.Exception ex) {
                System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] AFTER reflection failed: {ex.Message}\n");
            }

            // Check for errors
            System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] LaunchScriptAsync: command completed (checking for errors)\n");

            System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] LaunchScriptAsync: command completed, sending Terminated\n");
            _debugAdapterServer?.SendNotification(EventNames.Terminated);
        }
    }
}
