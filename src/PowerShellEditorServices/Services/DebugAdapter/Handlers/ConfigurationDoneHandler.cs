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
            PSCommand command;
            if (System.IO.File.Exists(scriptToLaunch))
            {
                // For a saved file we just execute its path (after escaping it).
                command = PSCommandHelpers.BuildDotSourceCommandWithArguments(
                    PSCommandHelpers.EscapeScriptFilePath(scriptToLaunch), _debugStateService?.Arguments);
            }
            else // It's a URI to an untitled script, or a raw script.
            {
                bool isScriptFile = _workspaceService.TryGetFile(scriptToLaunch, out ScriptFile untitledScript);
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

            // Fix: Set _debuggingMode on the TLS (Thread Local Storage) execution context.
            // The debugger checks _debuggingMode from the TLS context during script execution,
            // not from the runspace's debugger context. If TLS _debuggingMode is 0, the debugger
            // skips breakpoint checks entirely, even though breakpoints are registered.
            try
            {
                var localPipelineType = typeof(System.Management.Automation.Runspaces.Runspace).Assembly
                    .GetType("System.Management.Automation.Runspaces.LocalPipeline");
                var getCtxMethod = localPipelineType?.GetMethod(
                    "GetExecutionContextFromTLS",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                var tlsContext = getCtxMethod?.Invoke(null, null);
                if (tlsContext is not null)
                {
                    var execContextType = tlsContext.GetType();
                    var debuggingModeField = execContextType.GetField(
                        "_debuggingMode",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (debuggingModeField is not null)
                    {
                        // DebugModes.LocalScript = 1
                        debuggingModeField.SetValue(tlsContext, (System.Management.Automation.DebugModes)1);
                    }
                }
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Failed to set TLS _debuggingMode");
            }

            // Fix: Ensure _context.CurrentRunspace is set on the debugger's ExecutionContext.
            // In PSES with UseCurrentThread, the debugger's _context.CurrentRunspace can be null
            // which causes OnDebuggerStop to crash with NullReferenceException.
            try
            {
                var dbg = _runspaceContext.CurrentRunspace.Runspace.Debugger;
                var dbgType = dbg.GetType();
                var contextField = dbgType.GetField(
                    "_context", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (contextField is not null)
                {
                    var context = contextField.GetValue(dbg);
                    var currentRunspaceProp = context?.GetType().GetProperty(
                        "CurrentRunspace", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (currentRunspaceProp is not null && currentRunspaceProp.GetValue(context) is null)
                    {
                        currentRunspaceProp.SetValue(context, _runspaceContext.CurrentRunspace.Runspace);
                    }
                }
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Failed to fix null CurrentRunspace on debugger._context");
            }

            try
            {
                await _executionService.ExecutePSCommandAsync(
                    command,
                    CancellationToken.None,
                    s_debuggerExecutionOptions).ConfigureAwait(false);
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "LaunchScriptAsync: ExecutePSCommandAsync threw");
            }

            _debugAdapterServer?.SendNotification(EventNames.Terminated);
        }
    }
}
