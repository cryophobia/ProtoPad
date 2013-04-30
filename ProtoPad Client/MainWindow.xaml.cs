﻿using System;
using System.CodeDom.Compiler;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Navigation;
using ActiproSoftware.Text;
using ActiproSoftware.Text.Implementation;
using ActiproSoftware.Text.Languages.CSharp.Implementation;
using ActiproSoftware.Text.Languages.DotNet;
using ActiproSoftware.Text.Languages.DotNet.Ast.Implementation;
using ActiproSoftware.Text.Languages.DotNet.Reflection;
using ActiproSoftware.Text.Parsing;
using ActiproSoftware.Windows.Controls.SyntaxEditor.IntelliPrompt.Implementation;
using Microsoft.CSharp;
using ServiceDiscovery;
using mshtml;

namespace ProtoPad_Client
{    
    public partial class MainWindow
    {
        private IHTMLElement _htmlHolder;
        private IHTMLWindow2 _htmlWindow;
        private string _currentWrapText;
        private IProjectAssembly _projectAssembly;
        
        private List<string> _referencedAssemblies = new List<string>();
        private string _msCorLib = null;

        private EditorHelpers.CodeType _currentCodeType = EditorHelpers.CodeType.Statements;
        private DeviceItem _currentDevice;

        private string WrapHeader
        {
            get { return _currentWrapText.Split(new[] { EditorHelpers.CodeTemplateStatementsPlaceHolder }, StringSplitOptions.None)[0]; }
        }
        private string WrapFooter
        {
            get { return _currentWrapText.Split(new[] { EditorHelpers.CodeTemplateStatementsPlaceHolder }, StringSplitOptions.None)[1]; }
        }


        public enum DeviceTypes {Android, iOS, Local}

        public class DeviceItem
        {
            public string DeviceName { get; set; }
            public string DeviceAddress { get; set; }
            public string MainXamarinAssemblyName { get; set; }
            public DeviceTypes DeviceType;
        }

        public MainWindow()
        {
            InitializeComponent();

            _currentDevice = new DeviceItem
            {
                DeviceAddress = "__LOCAL__",
                DeviceType = DeviceTypes.Local,
                DeviceName = "Local"
            };

            SetText();
            InitializeEditor();
            InitializeResultWindow();
        }

        #region Event handlers

        private void SendCodeButton_Click(object sender, RoutedEventArgs e)
        {
            var result = SendCode(_currentDevice.DeviceAddress);
            if (result == null) return;
            var errorMessage = result.ErrorMessage;
            if (!String.IsNullOrWhiteSpace(errorMessage))
            {
                if (errorMessage.StartsWith("___EXCEPTION_____At offset: "))
                {
                    var exceptionBody = errorMessage.Substring("___EXCEPTION_____At offset: ".Length);
                    var exceptionParts = exceptionBody.Split(new[] {"__"}, StringSplitOptions.None);
                    var codeOffset = int.Parse(exceptionParts[0]) - 1;

                    var position = CodeEditor.Document.CurrentSnapshot.OffsetToPosition(codeOffset);

                    ShowLineError(position.Line+1, exceptionParts[1]);
                }
                LogToResultsWindow(errorMessage);
            }
            else if (result.Results != null)
            {
                _htmlHolder.innerHTML = String.Join("", result.Results.Select(r => "<h1>" + r.ResultKey + "</h1>" + DumpToXhtml.Dump(r.ResultValue, 0).ToString()));
                _htmlWindow.execScript("Update();", "javascript");
            }
        }

        private void LoadAssemblyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDevice == null)
            {
                MessageBox.Show("Please connect to an app first!");
                return;
            }
            var dlg = new Microsoft.Win32.OpenFileDialog { DefaultExt = ".dll" };

            var frameworkReferenceAssembliesDirectory = EditorHelpers.GetFrameworkReferenceAssembliesDirectory();
            switch (_currentDevice.DeviceType)
            {
                case DeviceTypes.Android:
                    dlg.Filter = "Xamarin.Android-compatible assembly (.dll)|*.dll";
                    dlg.InitialDirectory = Path.Combine(frameworkReferenceAssembliesDirectory, "MonoAndroid");
                    break;
                case DeviceTypes.iOS:
                    dlg.Filter = "Xamarin.iOS-compatible assembly (.dll)|*.dll";
                    dlg.InitialDirectory = Path.Combine(frameworkReferenceAssembliesDirectory, @"MonoTouch\v4.0");
                    break;
                case DeviceTypes.Local:
                    dlg.Filter = ".Net assembly (.dll)|*.dll";
                    dlg.InitialDirectory = Path.Combine(frameworkReferenceAssembliesDirectory, @".NETFramework");
                    break;
            }

            var result = dlg.ShowDialog();
            if (!result.Value) return;
            var assemblyPath = dlg.FileName;
            _projectAssembly.AssemblyReferences.AddFrom(assemblyPath);
            _referencedAssemblies.Add(assemblyPath);
            SimpleHttpServer.SendPostRequest(_currentDevice.DeviceAddress, File.ReadAllBytes(assemblyPath));
        }

        private void CodeTypeComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            switch (CodeTypeComboBox.SelectedValue.ToString())
            {
                case "C# Expression":
                    _currentCodeType = EditorHelpers.CodeType.Expression;
                    break;
                case "C# Statements":
                    _currentCodeType = EditorHelpers.CodeType.Statements;
                    break;
                case "C# Program":
                    _currentCodeType = EditorHelpers.CodeType.Program;
                    break;
            }
            SetText();
        }

        private void ClearSimulatorWindowButton_Click(object sender, RoutedEventArgs e)
        {
            var wrapText = EditorHelpers.GetWrapText(EditorHelpers.CodeType.Statements, _currentDevice.DeviceType);
            var clearCode = wrapText.Replace("__STATEMENTSHERE__", _currentDevice.DeviceType == DeviceTypes.iOS ? EditorHelpers.ClearWindowStatements_iOS : EditorHelpers.ClearWindowStatements_Android);
            SendCode(_currentDevice.DeviceAddress, false, clearCode);            
        }

        private void AboutHelpButton_Click(object sender, RoutedEventArgs e)
        {
            (new AboutWindow()).Show();
        }

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            var connectWindow = new ConnectWindow();
            var result = connectWindow.ShowDialog().Value;
            if (result)
            {
                ConnectToApp(connectWindow.SelectedDeviceItem);
            }
        }

        #endregion


        private ExecuteResponse SendCode(string url, bool wrapWithDefaultCode = true, string specialNonEditorCode = null)
        {
            var assemblyPath = CompileSource(wrapWithDefaultCode, specialNonEditorCode);
            if (String.IsNullOrWhiteSpace(assemblyPath)) return null;
            if (_currentDevice.DeviceType == DeviceTypes.Local)
            {
                var executeResponse = EditorHelpers.ExecuteLoadedAssemblyString(File.ReadAllBytes(assemblyPath));
                var dumpValues = executeResponse.GetDumpValues();
                if (dumpValues != null)
                {
                    executeResponse.Results = dumpValues.Select(v => new ResultPair(v.Description, Dumper.ObjectToDumpValue(v.Value, v.Level, executeResponse.GetMaxEnumerableItemCount()))).ToList();
                }
                return executeResponse;
            }
            var responseString = SimpleHttpServer.SendPostRequest(url, File.ReadAllBytes(assemblyPath)).Trim();
            return String.IsNullOrWhiteSpace(responseString) ? null : UtilityMethods.JsonDecode<ExecuteResponse>(responseString);
        }

        /// <summary>
        /// Get text offsets for all regular statements in the code.
        /// These will be used to insert special 'offset registration' statements
        /// Offset registrations are used to catch the location where runtime errors occur
        /// </summary>
        private static bool VisitNodesAndSelectStatementOffsets(IAstNode node, ICollection<int> statementOffsets)
        {
            if (node.Value == "SimpleName: \"DumpHelpers\"" && node.Parent.Value == "ClassDeclaration")
            {
                return false;
            }

            if (node is Statement && !(node is BlockStatement) && node.StartOffset.HasValue && node.StartOffset >= 0)
            {
                var isDumpMethodStatement = false;
                if (node.Parent != null && node.Parent.Parent != null)
                {
                    var methodNode = node.Parent.Parent as MethodDeclaration;
                    if (methodNode != null)
                    {
                        if (methodNode.Name.Text == "____TrackStatementOffset") isDumpMethodStatement = true;
                    }
                }

                if (!isDumpMethodStatement) statementOffsets.Add(node.StartOffset.Value);
            }
            return node.Children.All(childNode => VisitNodesAndSelectStatementOffsets(childNode, statementOffsets));
        }

        private string GetSourceWithOffsetRegistrationStatements()
        {
            var parseData = CodeEditor.Document.ParseData as IDotNetParseData;

            var statementOffsets = new List<int>();
            VisitNodesAndSelectStatementOffsets(parseData.Ast, statementOffsets);

            var inserts = statementOffsets.ToDictionary(o => o, o => String.Format("____TrackStatementOffset({0});", o));

            var documentWithOffsets = new EditorDocument();
            documentWithOffsets.SetText(parseData.Snapshot.Text);

            var options = new TextChangeOptions { OffsetDelta = TextChangeOffsetDelta.SequentialOnly };
            var change = documentWithOffsets.CreateTextChange(TextChangeTypes.Custom, options);
            foreach (var insert in inserts)
            {
                change.InsertText(insert.Key, insert.Value);
            }
            change.Apply();
            return documentWithOffsets.Text;
        }

        private string CompileSource(bool wrapWithDefaultCode, string specialNonEditorCode = null)
        {
            var codeWithOffsets = (specialNonEditorCode ?? GetSourceWithOffsetRegistrationStatements()).Replace("void Main(", "public void Main(");

            var sourceCode = wrapWithDefaultCode ? String.Format("{0}{1}{2}", WrapHeader.Replace("void Main(", "public void Main("), codeWithOffsets, WrapFooter) : codeWithOffsets;

            var useRegularMsCorLib = String.IsNullOrWhiteSpace(_msCorLib);
            var cpd = new CSharpCodeProvider();

            var compilerParameters = useRegularMsCorLib ? new CompilerParameters() : new CompilerParameters { CompilerOptions = "/nostdlib" };

            if (!useRegularMsCorLib) compilerParameters.ReferencedAssemblies.Add(_msCorLib);
            compilerParameters.ReferencedAssemblies.AddRange(_referencedAssemblies.ToArray());

            compilerParameters.GenerateExecutable = false;
            var compileResults = cpd.CompileAssemblyFromSource(compilerParameters, sourceCode);
            CodeEditor.Document.IndicatorManager.Clear<ErrorIndicatorTagger, ErrorIndicatorTag>();
            var errorStringBuilder = new StringBuilder();
            foreach (CompilerError error in compileResults.Errors)
            {
                var startLines = WrapHeader.Split('\n').Length;
                var codeLineNumber = (error.Line - startLines);
                ShowLineError(codeLineNumber, error.ErrorText);
                errorStringBuilder.AppendFormat("Error on line {0}: {1}\r\n", codeLineNumber, error.ErrorText);
            }
            if (!String.IsNullOrWhiteSpace(errorStringBuilder.ToString())) _htmlHolder.innerHTML = errorStringBuilder.ToString();
            return compileResults.Errors.Count > 0 ? null : compilerParameters.OutputAssembly;
        }

        private void ShowLineError(int codeLineNumber, string errorMessage)
        {
            if (codeLineNumber < 0 || codeLineNumber >= CodeEditor.ActiveView.CurrentSnapshot.Lines.Count) codeLineNumber = 0;
            var editorLine = CodeEditor.ActiveView.CurrentSnapshot.Lines[codeLineNumber];
            CodeEditor.ActiveView.Selection.StartOffset = editorLine.StartOffset;
            CodeEditor.ActiveView.Selection.SelectToLineEnd();
            var tag = new ErrorIndicatorTag { ContentProvider = new PlainTextContentProvider(errorMessage) };
            CodeEditor.Document.IndicatorManager.Add<ErrorIndicatorTagger, ErrorIndicatorTag>(CodeEditor.ActiveView.Selection.SnapshotRange, tag);
        }

        private void ConnectToApp(DeviceItem deviceItem)
        {
            _currentDevice = deviceItem;

            var isLocal = _currentDevice.DeviceType == DeviceTypes.Local;

            ClearSimulatorWindowButton.IsEnabled = !isLocal;

            if (isLocal)
            {
                LogToResultsWindow("Running locally (regular .Net)");
            }
            else
            {
                _currentDevice.MainXamarinAssemblyName = SimpleHttpServer.SendGetMainXamarinAssemblyName(_currentDevice.DeviceAddress);
                LogToResultsWindow("Connected to device '{0}' on [{1}]", _currentDevice.DeviceName, _currentDevice.DeviceAddress);
            }

            Title = String.Format("ProtoPad - {0}", _currentDevice.DeviceName);

            _projectAssembly.AssemblyReferences.Clear();

            _referencedAssemblies.Clear();

            SetText();

            var assemblyLoader = new BackgroundWorker();
            assemblyLoader.DoWork += DotNetProjectAssemblyReferenceLoader;
            assemblyLoader.RunWorkerAsync();



            SendCodeButton.IsEnabled = true;
            LoadAssemblyButton.IsEnabled = true;

            StatusLabel.Content = "";

            if (_currentDevice.DeviceType != DeviceTypes.iOS)
            {
                return; // todo: locate and provide Android Emulator file path if applicable
            }

            var wrapText = EditorHelpers.GetWrapText(EditorHelpers.CodeType.Expression, _currentDevice.DeviceType);
            var getFolderCode = wrapText.Replace("__STATEMENTSHERE__", "Environment.GetFolderPath(Environment.SpecialFolder.Personal)");
            var result = SendCode(_currentDevice.DeviceAddress, false, getFolderCode);
            if (result == null || result.Results == null) return;
            var folder = result.Results.FirstOrDefault();
            if (folder == null) return;
            StatusLabel.Content = folder.ResultValue.PrimitiveValue.ToString();
        }

        private void SetText()
        {
            if (_currentDevice == null) return;

            _currentWrapText = EditorHelpers.GetWrapText(_currentCodeType, _currentDevice.DeviceType);
            UpdateAssemblyReferences();
            CodeEditor.Document.SetText(EditorHelpers.GetDefaultCode(_currentCodeType, _currentDevice.DeviceType));
            CodeEditor.Document.SetHeaderAndFooterText(WrapHeader, WrapFooter);
        }

        private void LogToResultsWindow(string message, params object[] stringFormatArguments)
        {
            if (_htmlHolder == null) return;
            var formattedMessage = String.Format(message, stringFormatArguments);
            _htmlHolder.innerHTML = formattedMessage;
        }

        private void InitializeResultWindow()
        {
            ResultTextBox.Navigated += (sender, args) =>
            {
                var htmlDocument3 = ResultTextBox.Document as IHTMLDocument3;
                var htmlDocument2 = ResultTextBox.Document as IHTMLDocument2;
                _htmlHolder = htmlDocument3.getElementById("wrapallthethings");
                _htmlWindow = htmlDocument2.parentWindow;
            };
            ResultTextBox.NavigateToString(Properties.Resources.ResultHtmlWrap);
        }

        private void InitializeEditor()
        {
            // Initialize the project assembly (enables support for automated IntelliPrompt features)
            _projectAssembly = new CSharpProjectAssembly("ProtoPad Client");
            var assemblyLoader = new BackgroundWorker();
            assemblyLoader.DoWork += DotNetProjectAssemblyReferenceLoader;
            assemblyLoader.RunWorkerAsync();

            // Load the .NET Languages Add-on C# language and register the project assembly on it
            var language = new CSharpSyntaxLanguage();
            language.RegisterProjectAssembly(_projectAssembly);

            CodeEditor.Document.Language = language;

            CodeEditor.Document.Language.RegisterService(new IndicatorQuickInfoProvider());

            CodeEditor.PreviewKeyDown += (sender, args) =>
            {
                if (args.Key != Key.F5) return;
                SendCodeButton_Click(null, null);
                args.Handled = true;
            };
        }

        private void UpdateAssemblyReferences()
        {
            switch (_currentDevice.DeviceType)
            {
                case DeviceTypes.Android:
                    _referencedAssemblies = EditorHelpers.GetXamarinAndroidBaseAssemblies(_currentDevice.MainXamarinAssemblyName, out _msCorLib);
                    break;
                case DeviceTypes.iOS:
                    _referencedAssemblies = EditorHelpers.GetXamariniOSBaseAssemblies(_currentDevice.MainXamarinAssemblyName, out _msCorLib);
                    break;
                default:
                case DeviceTypes.Local:
                    _msCorLib = null;
                    _referencedAssemblies = EditorHelpers.GetRegularDotNetBaseAssemblies();
                    break;
            }
        }

        private void DotNetProjectAssemblyReferenceLoader(object sender, DoWorkEventArgs e)
        {
            _projectAssembly.AssemblyReferences.AddMsCorLib();
            UpdateAssemblyReferences();
            _referencedAssemblies.ForEach(a => _projectAssembly.AssemblyReferences.AddFrom(a));
        }
    }
}