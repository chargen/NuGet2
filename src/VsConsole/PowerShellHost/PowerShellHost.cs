using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using NuGet;
using NuGet.VisualStudio;
using NuGet.VisualStudio.Resources;

namespace NuGetConsole.Host.PowerShell.Implementation {
    internal abstract class PowerShellHost : IHost, IPathExpansion, IDisposable {
        private static readonly object _lockObject = new object();
        private readonly string _name;
        private readonly IRunspaceManager _runspaceManager;
        private readonly IPackageSourceProvider _packageSourceProvider;
        private readonly ISolutionManager _solutionManager;
        private readonly IVsPackageManagerFactory _packageManagerFactory;

        private IConsole _activeConsole;
        private Runspace _runspace;
        private NuGetPSHost _myHost;
        // indicates whether this host has been initialized. 
        // null = not initilized, true = initialized successfully, false = initialized unsuccessfully
        private bool? _initialized;

        // store the current command typed so far
        private ComplexCommand _complexCommand;

        public PowerShellHost(string name, IRunspaceManager runspaceManager) {
            _runspaceManager = runspaceManager;

            // TODO: Take these as ctor arguments
            _packageSourceProvider = ServiceLocator.GetInstance<IPackageSourceProvider>();
            _solutionManager = ServiceLocator.GetInstance<ISolutionManager>();
            _packageManagerFactory = ServiceLocator.GetInstance<IVsPackageManagerFactory>();

            _name = name;
            IsCommandEnabled = true;
        }

        protected Pipeline ExecutingPipeline { get; set; }

        /// <summary>
        /// The host is associated with a particular console on a per-command basis. 
        /// This gets set every time a command is executed on this host.
        /// </summary>
        protected IConsole ActiveConsole {
            get {
                return _activeConsole;
            }
            set {
                _activeConsole = value;
                if (_myHost != null) {
                    _myHost.ActiveConsole = value;
                }
            }
        }

        public bool IsCommandEnabled {
            get; private set;
        }

        protected Runspace Runspace {
            get {
                Debug.Assert(_initialized != null);
                return _runspace;
            }
        }

        private ComplexCommand ComplexCommand {
            get {
                if (_complexCommand == null) {
                    _complexCommand = new ComplexCommand((allLines, lastLine) => {
                        Collection<PSParseError> errors;
                        PSParser.Tokenize(allLines, out errors);

                        // If there is a parse error token whose END is past input END, consider
                        // it a multi-line command.
                        if (errors.Count > 0) {
                            if (errors.Any(e => (e.Token.Start + e.Token.Length) >= allLines.Length)) {
                                return false;
                            }
                        }

                        return true;
                    });
                }
                return _complexCommand;
            }
        }

        public string Prompt {
            get {
                return ComplexCommand.IsComplete ? "PM>" : ">> ";
            }
        }

        /// <summary>
        /// Doing all necessary initialization works before the console accepts user inputs
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        public void Initialize(IConsole console) {
            ActiveConsole = console;

            if (_initialized.HasValue) {
                if (_initialized.Value && console.ShowDisclaimerHeader) {
                    DisplayDisclaimerAndHelpText();
                }
            }
            else {
                try {
                    Tuple<Runspace, NuGetPSHost> tuple = _runspaceManager.GetRunspace(console, _name);
                    _runspace = tuple.Item1;
                    _myHost = tuple.Item2;

                    _initialized = true;

                    if (console.ShowDisclaimerHeader) {
                        DisplayDisclaimerAndHelpText();
                    }

                    ExecuteInitScripts();
                    _solutionManager.SolutionOpened += (o, e) => ExecuteInitScripts();
                }
                catch (Exception ex) {
                    // catch all exception as we don't want it to crash VS
                    _initialized = false;
                    IsCommandEnabled = false;
                    ReportError(ex);

                    ExceptionHelper.WriteToActivityLog(ex);
                }
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Microsoft.Design",
            "CA1031:DoNotCatchGeneralExceptionTypes",
            Justification = "We don't want execution of init scripts to crash our console.")]
        private void ExecuteInitScripts() {
            if (!String.IsNullOrEmpty(_solutionManager.SolutionDirectory)) {
                try {
                    var packageManager = (VsPackageManager)_packageManagerFactory.CreatePackageManager();
                    var localRepository = packageManager.LocalRepository;

                    // invoke init.ps1 files in the order of package dependency.
                    // if A -> B, we invoke B's init.ps1 before A's.

                    var sorter = new PackageSorter();
                    var sortedPackages = sorter.GetPackagesByDependencyOrder(localRepository);

                    foreach (var package in sortedPackages) {
                        string installPath = packageManager.PathResolver.GetInstallPath(package);

                        AddPathToEnvironment(Path.Combine(installPath, "tools"));
                        Runspace.ExecuteScript(installPath, "tools\\init.ps1", package);
                    }
                }
                catch (Exception ex) {
                    // if execution of Init scripts fails, do not let it crash our console
                    ReportError(ex);

                    ExceptionHelper.WriteToActivityLog(ex);
                }
            }
        }

        private static void AddPathToEnvironment(string path) {
            if (Directory.Exists(path)) {
                string environmentPath = Environment.GetEnvironmentVariable("path", EnvironmentVariableTarget.Process);
                environmentPath = environmentPath + ";" + path;
                Environment.SetEnvironmentVariable("path", environmentPath, EnvironmentVariableTarget.Process);
            }
        }

        protected abstract bool ExecuteHost(string fullCommand, string command, params object[] inputs);

        public bool Execute(IConsole console, string command, params object[] inputs) {
            if (console == null) {
                throw new ArgumentNullException("console");
            }

            if (command == null) {
                throw new ArgumentNullException("command");
            }

            ActiveConsole = console;

            string fullCommand;
            if (ComplexCommand.AddLine(command, out fullCommand) && !string.IsNullOrEmpty(fullCommand)) {                
                return ExecuteHost(fullCommand, command, inputs);
            }
            return false; // constructing multi-line command
        }

        public void Abort() {
            if (ExecutingPipeline != null) {
                ExecutingPipeline.StopAsync();
            }
            ComplexCommand.Clear();
        }

        protected void SetSyncModeOnHost(bool isSync) {
            if (_myHost != null) {
                PSPropertyInfo property = _myHost.PrivateData.Properties["IsSyncMode"];
                if (property == null) {
                    property = new PSNoteProperty("IsSyncMode", isSync);
                    _myHost.PrivateData.Properties.Add(property);
                }
                else {
                    property.Value = isSync;
                }
            }
        }

        public void SetDefaultRunspace() {
            if (Runspace.DefaultRunspace == null) {
                lock (_lockObject) {
                    if (Runspace.DefaultRunspace == null) {
                        // Set this runspace as DefaultRunspace so I can script DTE events.
                        //
                        // WARNING: MSDN says this is unsafe. The runspace must not be shared across
                        // threads. I need this to be able to use ScriptBlock for DTE events. The
                        // ScriptBlock event handlers execute on DefaultRunspace.

                        Runspace.DefaultRunspace = Runspace;
                    }
                }
            }
        }

        private void DisplayDisclaimerAndHelpText() {
            WriteLine(VsResources.Console_DisclaimerText);
            WriteLine();

            WriteLine(String.Format(CultureInfo.CurrentCulture, Resources.PowerShellHostTitle, _myHost.Version.ToString()));
            WriteLine();

            WriteLine(VsResources.Console_HelpText);
            WriteLine();
        }

        protected void ReportError(ErrorRecord record) {
            WriteErrorLine(Runspace.ExtractErrorFromErrorRecord(record));
        }

        protected void ReportError(Exception exception) {
            WriteErrorLine((exception.InnerException ?? exception).Message);
        }

        private void WriteErrorLine(string message) {
            if (ActiveConsole != null) {
                ActiveConsole.Write(message + Environment.NewLine, System.Windows.Media.Colors.Red, null);
            }
        }

        private void WriteLine(string message = "") {
            if (ActiveConsole != null) {
                ActiveConsole.WriteLine(message);
            }
        }

        public string ActivePackageSource {
            get {
                var activePackageSource = _packageSourceProvider.ActivePackageSource;
                return activePackageSource == null ? null : activePackageSource.Name;
            }
            set {
                if (string.IsNullOrEmpty(value)) {
                    throw new ArgumentNullException("value");
                }

                _packageSourceProvider.ActivePackageSource =
                    _packageSourceProvider.GetPackageSources().FirstOrDefault(
                        ps => ps.Name.Equals(value, StringComparison.OrdinalIgnoreCase));
            }
        }

        public string[] GetPackageSources() {
            return _packageSourceProvider.GetPackageSources().Select(ps => ps.Name).ToArray();
        }

        public string DefaultProject {
            get {
                Debug.Assert(_solutionManager != null);
                return _solutionManager.DefaultProjectName;
            }
            set {
                Debug.Assert(_solutionManager != null);
                _solutionManager.DefaultProjectName = value;
            }
        }

        public string[] GetAvailableProjects() {
            Debug.Assert(_solutionManager != null);

            var projectSafeNames = (_solutionManager.GetProjects().Select(p => _solutionManager.GetProjectSafeName(p))).ToArray();
            return projectSafeNames;
        }

        #region ITabExpansion
        public string[] GetExpansions(string line, string lastWord) {
            var query = from s in Runspace.Invoke(
                            "$__pc_args=@(); $input|%{$__pc_args+=$_}; TabExpansion $__pc_args[0] $__pc_args[1]; Remove-Variable __pc_args -Scope 0",
                            new string[] { line, lastWord },
                            outputResults: false)
                        select (s == null ? null : s.ToString());
            return query.ToArray();
        }
        #endregion

        #region IPathExpansion
        public SimpleExpansion GetPathExpansions(string line) {
            PSObject expansion = Runspace.Invoke(
                "$input|%{$__pc_args=$_}; _TabExpansionPath $__pc_args; Remove-Variable __pc_args -Scope 0",
                new object[] {line},
                outputResults: false).FirstOrDefault();
            if (expansion != null) {
                int replaceStart = (int)expansion.Properties["ReplaceStart"].Value;
                IList<string> paths = ((IEnumerable<object>)expansion.Properties["Paths"].Value).Select(o => o.ToString()).ToList();
                return new SimpleExpansion(replaceStart, line.Length - replaceStart, paths);
            }

            return null;
        }
        #endregion

        #region IDisposable
        public void Dispose() {
            if (_runspace != null) {
                _runspace.Dispose();
            }
        }
        #endregion
    }
}