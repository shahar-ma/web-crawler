using System.Net;

namespace UrlCrawler.Domain;

public static class PublicUrlPolicy
{
    public static bool TryParse(string? value, out Uri? uri, out string? error)
    {
        uri = null;
        error = null;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(parsed.Host) ||
            !string.IsNullOrEmpty(parsed.UserInfo))
        {
            error = "Enter an absolute HTTP or HTTPS URL without embedded credentials.";
            return false;
        }

        if (parsed.IsLoopback || parsed.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(parsed.Host, out var address) && !IsPublicAddress(address)))
        {
            error = "The crawler only accepts publicly routable hosts.";
            return false;
        }

        uri = parsed;
        return true;
    }

    public static async Task EnsurePublicHostAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(uri.Host, out var address))
        {
            if (!IsPublicAddress(address))
                throw new InvalidOperationException("The URL resolves to a non-public address.");
            return;
        }

        var addresses = await Dns.GetHostAddressesAsync(uri.Host, cancellationToken);
        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
            throw new InvalidOperationException("The URL resolves to a non-public address.");
    }

    public static bool IsSameOrigin(Uri root, Uri candidate) =>
        string.Equals(root.Scheme, candidate.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(root.Host, candidate.Host, StringComparison.OrdinalIgnoreCase) &&
        root.Port == candidate.Port;

    public static string Normalize(Uri uri)
    {
        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        if ((builder.Scheme == Uri.UriSchemeHttp && builder.Port == 80) ||
            (builder.Scheme == Uri.UriSchemeHttps && builder.Port == 443))
            builder.Port = -1;
        return builder.Uri.AbsoluteUri;
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (IsNonPublicAddress(address))
            return false;
        if (address.IsIPv4MappedToIPv6)
            return IsPublicAddress(address.MapToIPv4());

        var bytes = address.GetAddressBytes();
        return address.AddressFamily switch
        {
            System.Net.Sockets.AddressFamily.InterNetworkV6 => (bytes[0] & 0xFE) != 0xFC,
            System.Net.Sockets.AddressFamily.InterNetwork => IsPublicIpv4Address(bytes),
            _ => false
        };
    }

    private static bool IsNonPublicAddress(IPAddress address) =>
        IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) ||
        address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal ||
        address.IsIPv6Multicast || address.IsIPv6SiteLocal;

    private static bool IsPublicIpv4Address(byte[] bytes) =>
        !IsPrivateIpv4Address(bytes) && !IsReservedIpv4Address(bytes) && bytes[0] < 224;

    private static bool IsPrivateIpv4Address(byte[] bytes) =>
        bytes[0] is 0 or 10 or 127 ||
        (bytes[0] == 100 && (bytes[1] & 0xC0) == 64) ||
        (bytes[0] == 169 && bytes[1] == 254) ||
        (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
        (bytes[0] == 192 && bytes[1] == 168);

    private static bool IsReservedIpv4Address(byte[] bytes) =>
        (bytes[0] == 192 && bytes[1] == 0 && bytes[2] is 0 or 2) ||
        (bytes[0] == 198 && bytes[1] is 18 or 19) ||
        (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) ||
        (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
}
