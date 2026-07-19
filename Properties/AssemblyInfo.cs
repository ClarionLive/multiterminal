using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// Expose internal test seams (PresenceAdapter.InitializeForTest/EvaluateAt/HandleMessage, etc.)
// to the test assembly so integration tests can drive the parse→state-machine pipeline offline.
[assembly: InternalsVisibleTo("MultiTerminal.Tests")]

[assembly: AssemblyTitle("MultiTerminal")]
[assembly: AssemblyDescription("Multi-terminal application with grid layout support")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("MultiTerminal")]
[assembly: AssemblyCopyright("Copyright 2025 John Hickey")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

[assembly: ComVisible(false)]
[assembly: Guid("a1b2c3d4-e5f6-7890-abcd-ef1234567890")]

[assembly: AssemblyVersion("2.1.0.0")]
[assembly: AssemblyFileVersion("2.1.0.0")]
