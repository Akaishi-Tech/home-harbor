using System.Globalization;
using System.Text;
using HomeHarbor.Api.Data;

namespace HomeHarbor.Api.Services;

public sealed class SmbConfigService : ISmbConfigService
{
    public string BuildSmbConf(
        IEnumerable<SmbShareEntity> shares,
        IEnumerable<SmbCredentialEntity> credentials)
    {
        var activeCredentials = credentials
            .Where(c => c.Enabled && c.RevokedAt is null)
            .GroupBy(c => c.ShareId)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.UnixUser, StringComparer.Ordinal).ToList());

        var builder = new StringBuilder();
        _ = builder.AppendLine("[global]");
        _ = builder.AppendLine("   server role = standalone server");
        _ = builder.AppendLine("   workgroup = WORKGROUP");
        _ = builder.AppendLine("   netbios name = HOMEHARBOR");
        _ = builder.AppendLine("   security = user");
        _ = builder.AppendLine("   map to guest = never");
        _ = builder.AppendLine("   server min protocol = SMB3_00");
        _ = builder.AppendLine("   server signing = mandatory");
        _ = builder.AppendLine("   smb encrypt = required");
        _ = builder.AppendLine("   ntlm auth = ntlmv2-only");
        _ = builder.AppendLine("   passdb backend = tdbsam");
        _ = builder.AppendLine("   private dir = /var/lib/homeharbor/samba/private");
        _ = builder.AppendLine("   state directory = /var/lib/homeharbor/samba/state");
        _ = builder.AppendLine("   cache directory = /var/lib/homeharbor/samba/cache");
        _ = builder.AppendLine("   lock directory = /var/lib/homeharbor/samba/lock");
        _ = builder.AppendLine("   log file = /var/log/samba/homeharbor-%m.log");
        _ = builder.AppendLine("   max log size = 1000");
        _ = builder.AppendLine("   disable spoolss = yes");
        _ = builder.AppendLine("   load printers = no");
        _ = builder.AppendLine("   printing = bsd");
        _ = builder.AppendLine("   dns proxy = no");
        _ = builder.AppendLine("   smb ports = 445");
        _ = builder.AppendLine("   follow symlinks = no");
        _ = builder.AppendLine("   wide links = no");
        _ = builder.AppendLine("   unix extensions = no");
        _ = builder.AppendLine();

        foreach (var share in shares.Where(s => s.Enabled).OrderBy(s => s.ShareName, StringComparer.OrdinalIgnoreCase))
        {
            _ = activeCredentials.TryGetValue(share.Id, out var shareCredentials);
            shareCredentials ??= [];

            _ = builder.AppendLine(CultureInfo.InvariantCulture, $"[{EscapeSectionName(share.ShareName)}]");
            _ = builder.AppendLine(CultureInfo.InvariantCulture, $"   comment = {EscapeValue(share.Name)}");
            _ = builder.AppendLine(CultureInfo.InvariantCulture, $"   path = {EscapeValue(share.Path)}");
            _ = builder.AppendLine("   browseable = yes");
            _ = builder.AppendLine("   guest ok = no");
            _ = builder.AppendLine(CultureInfo.InvariantCulture, $"   read only = {(share.ReadOnly ? "yes" : "no")}");
            _ = builder.AppendLine("   force user = homeharbor");
            _ = builder.AppendLine("   force group = homeharbor");
            _ = builder.AppendLine("   create mask = 0660");
            _ = builder.AppendLine("   directory mask = 0770");

            if (shareCredentials.Count == 0)
            {
                _ = builder.AppendLine("   valid users = nobody");
            }
            else
            {
                _ = builder.AppendLine(CultureInfo.InvariantCulture, $"   valid users = {string.Join(' ', shareCredentials.Select(c => c.UnixUser))}");
                var readOnlyUsers = shareCredentials.Where(c => c.ReadOnly || share.ReadOnly).Select(c => c.UnixUser).ToArray();
                var writeUsers = share.ReadOnly
                    ? Array.Empty<string>()
                    : [.. shareCredentials.Where(c => !c.ReadOnly).Select(c => c.UnixUser)];
                if (readOnlyUsers.Length > 0) _ = builder.AppendLine(CultureInfo.InvariantCulture, $"   read list = {string.Join(' ', readOnlyUsers)}");
                if (writeUsers.Length > 0) _ = builder.AppendLine(CultureInfo.InvariantCulture, $"   write list = {string.Join(' ', writeUsers)}");
            }

            _ = builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string EscapeSectionName(string value)
        => value.Replace("[", string.Empty, StringComparison.Ordinal)
            .Replace("]", string.Empty, StringComparison.Ordinal)
            .Trim();

    private static string EscapeValue(string value)
        => value.ReplaceLineEndings(" ").Trim();
}
