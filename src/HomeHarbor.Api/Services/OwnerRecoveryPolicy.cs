using HomeHarbor.Api.Auth;
using HomeHarbor.Api.Data;
using HomeHarbor.Core.Identity;

namespace HomeHarbor.Api.Services;

internal static class OwnerRecoveryPolicy
{
    public static bool IsRecoveryCodeValid(FamilySpaceEntity family, string? recoveryCode)
        => LocalPasswordVerifier.Verify(recoveryCode, family.RecoveryCodeHash);

    public static FamilyMemberEntity? FindPrimaryOwner(
        FamilySpaceEntity family,
        IEnumerable<FamilyMemberEntity> members)
    {
        FamilyMemberEntity? primary = null;
        foreach (var member in members)
        {
            if (!IsPrimaryOwner(family, member) || member.Role != FamilyRoles.Owner) continue;
            if (primary is not null) return null;
            primary = member;
        }

        return primary;
    }

    public static bool IsPrimaryOwner(FamilySpaceEntity family, FamilyMemberEntity member)
        => family.Id == member.FamilyId &&
           !string.IsNullOrWhiteSpace(family.OwnerDisplayName) &&
           string.Equals(family.OwnerDisplayName, member.DisplayName, StringComparison.Ordinal);

    public static bool RequiresPhysicalLegacyEnrollment(
        FamilySpaceEntity family,
        FamilyMemberEntity owner)
        => IsPrimaryOwner(family, owner) &&
           owner.Role == FamilyRoles.Owner &&
           string.IsNullOrWhiteSpace(family.RecoveryCodeHash) &&
           string.IsNullOrWhiteSpace(owner.PasswordHash);

    public static bool TryRotateRecoveryCode(
        FamilySpaceEntity family,
        FamilyMemberEntity currentMember,
        string? currentPassword,
        ITokenGenerator tokenGenerator,
        out string recoveryCode)
    {
        recoveryCode = string.Empty;
        if (family.Id != currentMember.FamilyId ||
            currentMember.Role != FamilyRoles.Owner ||
            !LocalPasswordVerifier.Verify(currentPassword, currentMember.PasswordHash))
        {
            return false;
        }

        recoveryCode = tokenGenerator.GenerateRecoveryCode();
        family.RecoveryCodeHash = BCrypt.Net.BCrypt.HashPassword(recoveryCode);
        return true;
    }
}
