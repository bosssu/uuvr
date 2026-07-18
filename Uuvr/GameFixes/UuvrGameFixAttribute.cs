using System;

namespace Uuvr.GameFixes;

/// <summary>
/// Declares a code GameFix id (should match GameProfiles manifest id when both exist).
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class UuvrGameFixAttribute : Attribute
{
    public string Id { get; private set; }
    public int Priority { get; set; }

    public UuvrGameFixAttribute(string id)
    {
        Id = id ?? "";
    }
}
