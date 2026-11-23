using System.Security.Cryptography;
using System.Text;
using StandChillow.LanServer.Auth;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Личный пароль админа: формат <c>&lt;id&gt;-&lt;hex64&gt;</c>, SHA-256 на диске и
/// сравнение без раннего выхода. Пароль не должен нигде храниться в открытом виде.
/// </summary>
public sealed class AdminPasswordTests
{
    private const int AdminId = 1000;

    [Fact]
    public void GenerateSecret_IsLowercaseHexOfTheDocumentedLength()
    {
        var secret = AdminPassword.GenerateSecret();

        Assert.Equal(AdminPassword.SecretLength, secret.Length);
        Assert.All(secret, c => Assert.True(Uri.IsHexDigit(c)));
        Assert.Equal(secret.ToLowerInvariant(), secret);
        Assert.NotEqual(secret, AdminPassword.GenerateSecret());
    }

    [Fact]
    public void Format_BuildsThePasswordFromIdAndSecret()
    {
        var secret = new string('a', AdminPassword.SecretLength);

        Assert.Equal(
            $"{AdminId}-{secret}",
            AdminPassword.Format(AdminId, secret));
    }

    [Fact]
    public void TryParse_AcceptsFullPasswordAndBareSecret()
    {
        var secret = AdminPassword.GenerateSecret();
        var full = AdminPassword.Format(AdminId, secret);

        Assert.True(AdminPassword.TryParse(AdminId, full, out var fromFull));
        Assert.True(AdminPassword.TryParse(AdminId, $" {secret} ", out var fromBare));

        Assert.Equal(full, fromFull);
        Assert.Equal(full, fromBare);
    }

    [Fact]
    public void TryParse_NormalizesUppercaseHex()
    {
        var secret = AdminPassword.GenerateSecret().ToUpperInvariant();
        var full = $"{AdminId}-{secret}";

        Assert.True(AdminPassword.TryParse(AdminId, full, out var normalized));

        Assert.Equal(
            $"{AdminId}-{secret.ToLowerInvariant()}",
            normalized);
    }

    [Theory]
    [InlineData("1001-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("1000-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("1000-zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("1000-")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_RejectsForeignIdWrongLengthAndNonHex(string? input)
    {
        Assert.False(AdminPassword.TryParse(AdminId, input, out _));
    }

    [Fact]
    public void Hash_IsSha256Hex()
    {
        const string password = "1000-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        var expected = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(password))).ToLowerInvariant();

        Assert.Equal(expected, AdminPassword.Hash(password));
        Assert.Equal(AdminPassword.SecretLength, AdminPassword.Hash(password).Length);
    }

    [Fact]
    public void Matches_AcceptsFullAndBarePassword()
    {
        var full = AdminPassword.Format(AdminId, AdminPassword.GenerateSecret());
        var hash = AdminPassword.Hash(full);
        var bare = full[(full.IndexOf('-') + 1)..];

        Assert.True(AdminPassword.Matches(AdminId, full, hash));
        Assert.True(AdminPassword.Matches(AdminId, bare, hash));
        Assert.True(AdminPassword.Matches(AdminId, full.ToUpperInvariant(), hash));
    }

    [Theory]
    [InlineData(1001, "другой id")]
    [InlineData(0, "нулевой id")]
    public void Matches_RejectsForeignId(int id, string why)
    {
        var full = AdminPassword.Format(AdminId, AdminPassword.GenerateSecret());
        var hash = AdminPassword.Hash(full);

        Assert.False(AdminPassword.Matches(id, full, hash), why);
    }

    [Fact]
    public void Matches_RejectsWrongPasswordAndBrokenHash()
    {
        var full = AdminPassword.Format(AdminId, AdminPassword.GenerateSecret());
        var hash = AdminPassword.Hash(full);
        var other = AdminPassword.Format(AdminId, AdminPassword.GenerateSecret());

        Assert.False(AdminPassword.Matches(AdminId, other, hash));
        Assert.False(AdminPassword.Matches(AdminId, full, "не-хеш"));
        Assert.False(AdminPassword.Matches(AdminId, full, ""));
        Assert.False(AdminPassword.Matches(AdminId, full, null));
    }
}
