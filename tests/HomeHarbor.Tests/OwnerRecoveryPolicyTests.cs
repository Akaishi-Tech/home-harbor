using System.Reflection;
using HomeHarbor.Api.Auth;
using HomeHarbor.Api.Controllers;
using HomeHarbor.Api.Data;
using HomeHarbor.Api.Services;
using HomeHarbor.Core.Identity;
using Microsoft.AspNetCore.Authorization;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class OwnerRecoveryPolicyTests
{
    [TestMethod]
    public void Legacy_Family_Without_Recovery_Hash_Can_Rotate_After_Owner_Password_Verification()
    {
        var family = Family("Primary Owner");
        var owner = Member(family.Id, "Primary Owner", FamilyRoles.Owner, "correct-current-password");
        var tokens = new FixedTokenGenerator("NEW1-RECO-VERY-CODE");

        Assert.IsFalse(OwnerRecoveryPolicy.IsRecoveryCodeValid(family, "old-code"));
        Assert.IsTrue(OwnerRecoveryPolicy.TryRotateRecoveryCode(
            family,
            owner,
            "correct-current-password",
            tokens,
            out var recoveryCode));

        Assert.AreEqual("NEW1-RECO-VERY-CODE", recoveryCode);
        Assert.IsTrue(OwnerRecoveryPolicy.IsRecoveryCodeValid(family, recoveryCode));
        Assert.AreEqual(1, tokens.RecoveryCodeCalls);
    }

    [TestMethod]
    public void Recovery_Code_Rotation_Rejects_NonOwner_And_Wrong_Current_Password()
    {
        var family = Family("Primary Owner");
        var admin = Member(family.Id, "Admin", FamilyRoles.Admin, "correct-current-password");
        var owner = Member(family.Id, "Primary Owner", FamilyRoles.Owner, "correct-current-password");
        var tokens = new FixedTokenGenerator("NEW1-RECO-VERY-CODE");

        Assert.IsFalse(OwnerRecoveryPolicy.TryRotateRecoveryCode(
            family,
            admin,
            "correct-current-password",
            tokens,
            out _));
        Assert.IsFalse(OwnerRecoveryPolicy.TryRotateRecoveryCode(
            family,
            owner,
            "wrong-current-password",
            tokens,
            out _));

        Assert.IsNull(family.RecoveryCodeHash);
        Assert.AreEqual(0, tokens.RecoveryCodeCalls);
    }

    [TestMethod]
    public void Recovery_Always_Selects_The_Initial_Primary_Owner_By_Unique_Display_Name()
    {
        var family = Family("Initial Owner");
        var secondaryOwner = Member(family.Id, "Later Owner", FamilyRoles.Owner, "secondary-password");
        var primaryOwner = Member(family.Id, "Initial Owner", FamilyRoles.Owner, "primary-password");

        var selected = OwnerRecoveryPolicy.FindPrimaryOwner(family, [secondaryOwner, primaryOwner]);

        Assert.AreEqual(primaryOwner.Id, selected?.Id);
        Assert.IsTrue(OwnerRecoveryPolicy.IsPrimaryOwner(family, primaryOwner));
        Assert.IsFalse(OwnerRecoveryPolicy.IsPrimaryOwner(family, secondaryOwner));
    }

    [TestMethod]
    public void Recovery_Fails_Closed_If_Primary_Owner_Identity_Is_Ambiguous_Or_Not_An_Owner()
    {
        var family = Family("Initial Owner");
        var first = Member(family.Id, "Initial Owner", FamilyRoles.Owner, "first-password");
        var duplicate = Member(family.Id, "Initial Owner", FamilyRoles.Owner, "duplicate-password");
        var demoted = Member(family.Id, "Initial Owner", FamilyRoles.Admin, "admin-password");

        Assert.IsNull(OwnerRecoveryPolicy.FindPrimaryOwner(family, [first, duplicate]));
        Assert.IsNull(OwnerRecoveryPolicy.FindPrimaryOwner(family, [demoted]));
    }

    [TestMethod]
    public void Physical_Legacy_Enrollment_Is_Limited_To_Primary_Owner_With_Both_Hashes_Missing()
    {
        var family = Family("Initial Owner");
        var owner = Member(family.Id, "Initial Owner", FamilyRoles.Owner, "temporary");
        owner.PasswordHash = null;

        Assert.IsTrue(OwnerRecoveryPolicy.RequiresPhysicalLegacyEnrollment(family, owner));

        owner.PasswordHash = BCrypt.Net.BCrypt.HashPassword("already-enrolled");
        Assert.IsFalse(OwnerRecoveryPolicy.RequiresPhysicalLegacyEnrollment(family, owner));
        owner.PasswordHash = null;
        family.RecoveryCodeHash = BCrypt.Net.BCrypt.HashPassword("existing-recovery");
        Assert.IsFalse(OwnerRecoveryPolicy.RequiresPhysicalLegacyEnrollment(family, owner));
    }

    [TestMethod]
    public void Rotate_Endpoint_Requires_FamilyOwner_Policy()
    {
        var method = typeof(IdentityController).GetMethod(
            nameof(IdentityController.RotateRecoveryCode),
            BindingFlags.Instance | BindingFlags.Public);

        var authorize = method?.GetCustomAttributes<AuthorizeAttribute>()
            .SingleOrDefault(attribute => attribute.Policy == AuthorizationPolicies.FamilyOwner);

        Assert.IsNotNull(authorize);
    }

    private static FamilySpaceEntity Family(string ownerDisplayName)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = "Family",
            OwnerDisplayName = ownerDisplayName,
            RecoveryCodeHash = null,
            CreatedAt = DateTimeOffset.UtcNow
        };

    private static FamilyMemberEntity Member(Guid familyId, string displayName, string role, string password)
        => new()
        {
            Id = Guid.NewGuid(),
            FamilyId = familyId,
            DisplayName = displayName,
            Role = role,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            CreatedAt = DateTimeOffset.UtcNow
        };

    private sealed class FixedTokenGenerator(string recoveryCode) : ITokenGenerator
    {
        public int RecoveryCodeCalls { get; private set; }

        public string GenerateRecoveryCode()
        {
            RecoveryCodeCalls++;
            return recoveryCode;
        }

        public string GenerateUsername(string prefix = "hh") => throw new NotSupportedException();
        public string GenerateSecret(int byteLength = 32) => throw new NotSupportedException();
    }
}
