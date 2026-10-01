namespace MultiTerminal.Dialogs
{
    /// <summary>
    /// The save rule for the Settings dialog's masked secret boxes (Multi-Connect phone password,
    /// notification secret, relay ApiKey). Task 3f0f6983.
    ///
    /// The dialog empties a placeholder-showing box on focus, which makes a bad value rarer but
    /// cannot rule it out: the user can still type or paste text that itself contains the dots,
    /// or reach some other path that leaves them in the box. Written back, that replaces the real
    /// secret with garbage. So a value containing the placeholder anywhere is never saved: a real
    /// secret never contains it, and refusing it keeps the stored secret unchanged. This rule is
    /// the guarantee; the focus handling is not.
    /// </summary>
    internal static class SecretBoxRules
    {
        /// <summary>
        /// True when the box's value should be written to settings: the user edited the box this
        /// session and the value carries no trace of the placeholder. An empty edited value IS
        /// written, which is how a user clears a stored secret.
        /// </summary>
        public static bool ShouldWrite(bool dirty, string value, string placeholder)
        {
            if (!dirty) return false;
            if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(placeholder)) return true;
            return !value.Contains(placeholder, System.StringComparison.Ordinal);
        }
    }
}
