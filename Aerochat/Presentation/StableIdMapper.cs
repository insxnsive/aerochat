using System.Globalization;

namespace Aerochat.Presentation;

/// <summary>
/// Maps transport-level identity strings onto the presentation layer's ulong ids
/// deterministically. Decimal numeric ids pass through unchanged so demo/local
/// data keeps its existing ids; GUID ids (what the self-hostable server emits)
/// are folded into the full unsigned range with a truncated 64-bit FNV-1a hash
/// so every distinct remote identity maps to one stable local id for the whole
/// process lifetime.
/// </summary>
public static class StableIdMapper
{
    public static ulong Map(Guid id) => MapCore(id.ToByteArray());

    public static bool TryMap(string? value, out ulong mapped)
    {
        mapped = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out mapped))
            return true;

        if (Guid.TryParse(value, out Guid guid))
        {
            mapped = Map(guid);
            return true;
        }

        return false;
    }

    private static ulong MapCore(byte[] bytes)
    {
        unchecked
        {
            const ulong FnvOffsetBasis = 14695981039346656037UL;
            const ulong FnvPrime = 1099511628211UL;
            ulong hash = FnvOffsetBasis;
            foreach (byte b in bytes)
            {
                hash ^= b;
                hash *= FnvPrime;
            }

            return hash;
        }
    }
}
