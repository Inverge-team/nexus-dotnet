// Compiler-support attributes that netstandard2.0 does not ship.
//
// `record` types and `init`-only setters require IsExternalInit; the nullable
// analysis attributes let the same source compile with identical null-flow
// warnings on every target. These are internal, so they never leak into the
// public surface of the package and cannot clash with a consumer's own copies.
//
// Linked into each shipping project only when TargetFramework is netstandard2.0
// (see Directory.Build.props).

namespace System.Runtime.CompilerServices
{
    using System.ComponentModel;

    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit
    {
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Parameter)]
    internal sealed class NotNullWhenAttribute : Attribute
    {
        public NotNullWhenAttribute(bool returnValue) => ReturnValue = returnValue;

        public bool ReturnValue { get; }
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.ReturnValue)]
    internal sealed class MaybeNullAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, Inherited = false)]
    internal sealed class MemberNotNullAttribute : Attribute
    {
        public MemberNotNullAttribute(string member) => Members = new[] { member };

        public MemberNotNullAttribute(params string[] members) => Members = members;

        public string[] Members { get; }
    }
}
