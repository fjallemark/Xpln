using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Tellurian.Trains.Models.Planning
{
    public sealed record Message : IEquatable<Message>
    {
        public static Message Information(string text) => new (text, Severity.Information);
        public static Message Information(string format, params object[] args) => new(string.Format(CultureInfo.CurrentCulture, format, args), Severity.Information);
        public static Message Warning(string text) => new (text, Severity.Warning);

        public static Message Warning(string format, params object[] args) => new (string.Format(CultureInfo.CurrentCulture, format, args), Severity.Warning);
        public static Message Error(string text) => new (text, Severity.Error);
        public static Message Error(string format, params object[] args) => new (string.Format(CultureInfo.CurrentCulture, format, args), Severity.Error);
        public static Message System(string text) => new (text, Severity.System);

        private Message(string text, Severity severity) { Text = text; Severity = severity; }

        public string Text { get; }
        public Severity Severity { get; }

        public override string ToString()=>
            $"{Severity.ToLanguageString(CultureInfo.CurrentCulture)}: {Text}";

        public bool Equals(Message? other) =>
            Severity.Equals(other?.Severity) && Text.Equals(other?.Text, StringComparison.OrdinalIgnoreCase);
        public override int GetHashCode() => HashCode.Combine(Text, Severity);
    }

    public enum Severity
    {
        Information = 0,
        Warning = 1,
        Error = 2,
        System = 3
    }

    public static class ErrorMessageExtensions
    {
        public static bool HasNoStoppingErrors(this IEnumerable<Message> me) => !me.Any() || me.Any(m => m.Severity < Severity.Error);
        public static bool HasStoppingErrors(this IEnumerable<Message> me) => me.Any(m => m.Severity >= Severity.Error);
        public static bool Contains(this IEnumerable<Message> me, string text) => me.Any(m => m.Text.Contains(text, StringComparison.OrdinalIgnoreCase));
        public static IEnumerable<string> ToStrings(this IEnumerable<Message> me) =>
            me is null ? Array.Empty<string>() : me.Select(m => m.ToString());
    }

    internal static class SeverityExtensions
    {
        public static string ToLanguageString(this Severity me, CultureInfo culture)=>
            Resources.Strings.ResourceManager.GetString(me.ToString(), culture) ?? string.Empty;
    }
}
