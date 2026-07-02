// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.PowerShell.EditorServices.Logging;
using Microsoft.PowerShell.EditorServices.Services;
using Microsoft.PowerShell.EditorServices.Services.DebugAdapter;
using Microsoft.PowerShell.EditorServices.Services.PowerShell.Runspace;
using Microsoft.PowerShell.EditorServices.Services.TextDocument;
using Microsoft.PowerShell.EditorServices.Utility;
using OmniSharp.Extensions.DebugAdapter.Protocol.Models;
using OmniSharp.Extensions.DebugAdapter.Protocol.Requests;

namespace Microsoft.PowerShell.EditorServices.Handlers
{
    internal class BreakpointHandlers : ISetFunctionBreakpointsHandler, ISetBreakpointsHandler, ISetExceptionBreakpointsHandler
    {
        private static readonly string[] s_supportedDebugFileExtensions = new[]
        {
            ".ps1",
            ".psm1"
        };

        private readonly ILogger _logger;
        private readonly DebugService _debugService;
        private readonly DebugStateService _debugStateService;
        private readonly WorkspaceService _workspaceService;
        private readonly IRunspaceContext _runspaceContext;

        public BreakpointHandlers(
            ILoggerFactory loggerFactory,
            DebugService debugService,
            DebugStateService debugStateService,
            WorkspaceService workspaceService,
            IRunspaceContext runspaceContext)
        {
            _logger = loggerFactory.CreateLogger<BreakpointHandlers>();
            _debugService = debugService;
            _debugStateService = debugStateService;
            _workspaceService = workspaceService;
            _runspaceContext = runspaceContext;
        }

        public async Task<SetBreakpointsResponse> Handle(SetBreakpointsArguments request, CancellationToken cancellationToken)
        {
            System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] BreakpointHandlers.Handle called, path='{request.Source.Path}'\n");
            if (!_workspaceService.TryGetFile(request.Source.Path, out ScriptFile scriptFile))
            {
                string message = _debugStateService.NoDebug ? string.Empty : "Source file could not be accessed, breakpoint not set.";
                IEnumerable<Breakpoint> srcBreakpoints = request.Breakpoints
                    .Select(srcBkpt => LspDebugUtils.CreateBreakpoint(
                        srcBkpt, request.Source.Path, message, verified: _debugStateService.NoDebug));

                // Return non-verified breakpoint message.
                return new SetBreakpointsResponse
                {
                    Breakpoints = new Container<Breakpoint>(srcBreakpoints)
                };
            }

            // Verify source file is a PowerShell script file.
            if (!IsFileSupportedForBreakpoints(request.Source.Path, scriptFile))
            {
                _logger.LogWarning(
                    $"Attempted to set breakpoints on a non-PowerShell file: {request.Source.Path}");

                string message = _debugStateService.NoDebug ? string.Empty : "Source is not a PowerShell script, breakpoint not set.";

                IEnumerable<Breakpoint> srcBreakpoints = request.Breakpoints
                    .Select(srcBkpt => LspDebugUtils.CreateBreakpoint(
                        srcBkpt, request.Source.Path, message, verified: _debugStateService.NoDebug));

                // Return non-verified breakpoint message.
                return new SetBreakpointsResponse
                {
                    Breakpoints = new Container<Breakpoint>(srcBreakpoints)
                };
            }

            // At this point, the source file has been verified as a PowerShell script.
            // Use the DocumentUri (which matches what LaunchScriptAsync passes to
            // Parser.ParseInput) so line breakpoints match the script block's Extent.File.
            // Normalize file:///scripts/... URIs to pspath:// URIs so the debugger's
            // functionContext._file matches the breakpoint's Script property.
            string breakpointScriptPath = NormalizeScriptUri(scriptFile.DocumentUri.ToString());
            IReadOnlyList<BreakpointDetails> breakpointDetails = request.Breakpoints
                .Select((srcBreakpoint) => BreakpointDetails.Create(
                    breakpointScriptPath,
                    srcBreakpoint.Line,
                    srcBreakpoint.Column,
                    srcBreakpoint.Condition,
                    srcBreakpoint.HitCondition,
                    srcBreakpoint.LogMessage)).ToList();

            System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] BreakpointHandlers: FilePath='{scriptFile.FilePath}', DocumentUri='{scriptFile.DocumentUri}', breakpointScriptPath='{breakpointScriptPath}', #breakpoints={breakpointDetails.Count}, line={breakpointDetails[0].LineNumber}\n");

            // If this is a "run without debugging (Ctrl+F5)" session ignore requests to set breakpoints.
            IReadOnlyList<BreakpointDetails> updatedBreakpointDetails = breakpointDetails;
            System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] BreakpointHandlers: NoDebug={_debugStateService.NoDebug}\n");
            if (!_debugStateService.NoDebug)
            {
                System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] BreakpointHandlers: calling WaitForSetBreakpointHandleAsync\n");
                await _debugStateService.WaitForSetBreakpointHandleAsync().ConfigureAwait(false);
                System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] BreakpointHandlers: calling SetLineBreakpointsAsync\n");

                try
                {
                    // The debugger's DebugMode may be Default or RemoteScript, neither of
                    // which support SetLineBreakpoint. Use reflection to set it to Local so
                    // breakpoints can be registered before the script launches.
                    var debugger = _runspaceContext.CurrentRunspace.Runspace.Debugger;
                    var debugModeProp = typeof(System.Management.Automation.Debugger).GetProperty("DebugMode");
                    var currentMode = (System.Management.Automation.DebugModes)debugModeProp!.GetValue(debugger)!;
                    if ((currentMode & System.Management.Automation.DebugModes.LocalScript) == 0)
                    {
                        debugModeProp.SetValue(debugger, currentMode | System.Management.Automation.DebugModes.LocalScript);
                        System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] BreakpointHandlers: added LocalScript to DebugMode (was {currentMode})\n");
                    }

                    updatedBreakpointDetails =
                        await _debugService.SetLineBreakpointsAsync(
                            scriptFile,
                            breakpointDetails).ConfigureAwait(false);
                    System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] BreakpointHandlers: SetLineBreakpointsAsync returned {updatedBreakpointDetails.Count} breakpoints, verified={updatedBreakpointDetails.FirstOrDefault()?.Verified}\n");

                    // Re-call SetDebugMode after breakpoints are registered. The debugger's
                    // SetDebugMode internally checks if _idToBreakpoint is non-empty and sets
                    // _context._debuggingMode to Enabled. If SetDebugMode was called before
                    // breakpoints were added, _debuggingMode stays 0 and breakpoints are never
                    // checked during execution.
                    debugger.SetDebugMode(System.Management.Automation.DebugModes.LocalScript | System.Management.Automation.DebugModes.RemoteScript);
                    System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] BreakpointHandlers: re-called SetDebugMode after breakpoint registration\n");
                }
                catch (Exception e)
                {
                    System.IO.File.AppendAllText("/tmp/pses-debug.log", $"[PSES] BreakpointHandlers: EXCEPTION: {e}\n");
                    // Log whatever the error is
                    _logger.LogException($"Caught error while setting breakpoints in SetBreakpoints handler for file {scriptFile?.FilePath}", e);
                }
                finally
                {
                    _debugStateService.ReleaseSetBreakpointHandle();
                }
            }

            return new SetBreakpointsResponse
            {
                Breakpoints = new Container<Breakpoint>(updatedBreakpointDetails
                    .Select(LspDebugUtils.CreateBreakpoint))
            };
        }

        public async Task<SetFunctionBreakpointsResponse> Handle(SetFunctionBreakpointsArguments request, CancellationToken cancellationToken)
        {
            IReadOnlyList<CommandBreakpointDetails> breakpointDetails = request.Breakpoints
                .Select((funcBreakpoint) => CommandBreakpointDetails.Create(
                    funcBreakpoint.Name,
                    funcBreakpoint.Condition)).ToList();

            // If this is a "run without debugging (Ctrl+F5)" session ignore requests to set breakpoints.
            IReadOnlyList<CommandBreakpointDetails> updatedBreakpointDetails = breakpointDetails;
            if (!_debugStateService.NoDebug)
            {
                await _debugStateService.WaitForSetBreakpointHandleAsync().ConfigureAwait(false);

                try
                {
                    updatedBreakpointDetails = await _debugService.SetCommandBreakpointsAsync(breakpointDetails).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    // Log whatever the error is
                    _logger.LogException("Caught error while setting command breakpoints", e);
                }
                finally
                {
                    _debugStateService.ReleaseSetBreakpointHandle();
                }
            }

            return new SetFunctionBreakpointsResponse
            {
                Breakpoints = updatedBreakpointDetails.Select(LspDebugUtils.CreateBreakpoint).ToList()
            };
        }

        public Task<SetExceptionBreakpointsResponse> Handle(SetExceptionBreakpointsArguments request, CancellationToken cancellationToken) =>
            // TODO: When support for exception breakpoints (unhandled and/or first chance)
            //       is added to the PowerShell engine, wire up the VSCode exception
            //       breakpoints here using the pattern below to prevent bug regressions.
            //if (!noDebug)
            //{
            //    setBreakpointInProgress = true;

            //    try
            //    {
            //        // Set exception breakpoints in DebugService
            //    }
            //    catch (Exception e)
            //    {
            //        // Log whatever the error is
            //        Logger.WriteException($"Caught error while setting exception breakpoints", e);
            //    }
            //    finally
            //    {
            //        setBreakpointInProgress = false;
            //    }
            //}

            Task.FromResult(new SetExceptionBreakpointsResponse());

        private bool IsFileSupportedForBreakpoints(string requestedPath, ScriptFile resolvedScriptFile)
        {
            // PowerShell 7 and above support breakpoints in untitled files
            if (ScriptFile.IsUntitledPath(requestedPath))
            {
                return BreakpointApiUtils.SupportsBreakpointApis(_runspaceContext.CurrentRunspace);
            }

            if (string.IsNullOrEmpty(resolvedScriptFile?.FilePath))
            {
                return false;
            }

            string fileExtension = Path.GetExtension(resolvedScriptFile.FilePath);
            return s_supportedDebugFileExtensions.Contains(fileExtension, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Normalizes file:///scripts/... URIs to pspath:// URIs so that the debugger's
        /// functionContext._file (set from the ScriptBlock's Extent.File) matches the
        /// breakpoint's Script property. PowerShell's debugger uses _file to look up
        /// pending breakpoints, and file:/// URIs get resolved to empty strings in the
        /// function context, while pspath:// URIs are preserved.
        /// </summary>
        internal static string NormalizeScriptUri(string uri)
        {
            if (string.IsNullOrEmpty(uri) || !uri.StartsWith("file:///scripts/"))
                return uri;

            var parts = uri.Replace("file:///scripts/", "").Split('/');
            if (parts.Length < 3)
                return uri;

            var scope = parts[0].ToLowerInvariant();
            var categoryPlural = parts[1];
            var fileName = string.Join("/", parts.Skip(2));

            var category = categoryPlural switch
            {
                "Functions" => "Function",
                "Immy%20System" => "ImmySystem",
                "Inventory" => "DeviceInventory",
                _ => categoryPlural,
            };

            return $"pspath://ScriptPSProvider/{scope}/{category}/{fileName}";
        }
    }
}
