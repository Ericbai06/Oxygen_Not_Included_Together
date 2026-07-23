using System;

namespace ONI_Together.DebugTools
{

    [AttributeUsage(AttributeTargets.Method)]
    public class UnitTestAttribute : Attribute
    {
        public string Name { get; }
        public string Category { get; }
        public bool LiveSafe { get; }
        public string HeadlessUnsupportedReason { get; }

        public UnitTestAttribute(
            string name = null,
            string category = "General",
            bool liveSafe = false,
            string headlessUnsupportedReason = null)
        {
            if (headlessUnsupportedReason != null &&
                string.IsNullOrWhiteSpace(headlessUnsupportedReason))
                throw new ArgumentException(
                    "Headless unsupported reason must be nonempty",
                    nameof(headlessUnsupportedReason));
            Name = name;
            Category = category;
            LiveSafe = liveSafe;
            HeadlessUnsupportedReason = headlessUnsupportedReason;
        }
    }
}
