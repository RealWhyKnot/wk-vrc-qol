// LoomDiagnostic.cs
//
// Validator output. Each diagnostic carries a severity, a message, and an
// optional UnityEngine.Object context so the inspector can ping the
// offending Thread / action when the user clicks the line. Mirrors the
// shape of Unity's ConsoleWindow log entries so the Loom window can
// render the list with the same affordances users expect.

using UnityEngine;

namespace WhyKnot.AvatarQol.Loom.Pipeline {

    internal enum LoomDiagnosticSeverity { Info, Warning, Error }

    internal sealed class LoomDiagnostic {
        public LoomDiagnosticSeverity Severity;
        public string Message;
        public Object Context;

        public LoomDiagnostic(LoomDiagnosticSeverity severity, string message, Object context = null) {
            Severity = severity;
            Message = message;
            Context = context;
        }

        public static LoomDiagnostic Error(string message, Object context = null)
            => new LoomDiagnostic(LoomDiagnosticSeverity.Error, message, context);

        public static LoomDiagnostic Warning(string message, Object context = null)
            => new LoomDiagnostic(LoomDiagnosticSeverity.Warning, message, context);

        public static LoomDiagnostic Info(string message, Object context = null)
            => new LoomDiagnostic(LoomDiagnosticSeverity.Info, message, context);
    }
}
