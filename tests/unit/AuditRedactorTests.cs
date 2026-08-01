using System.Text.Json;
using TeamsPhoneMcp.Audit;

namespace TeamsPhoneMcp.UnitTests;

public class AuditRedactorTests
{
    [Fact]
    public void Redact_ReplacesManifestDeclaredParameters()
    {
        var parameters = JsonSerializer.SerializeToElement(new
        {
            userUpn = "user@contoso.com",
            applicationSecret = "not-checked-by-name-list",
        });

        var redacted = AuditRedactor.Redact(parameters, ["applicationSecret"]);

        Assert.Equal("user@contoso.com", redacted.GetProperty("userUpn").GetString());
        Assert.Equal(AuditRedactor.RedactedPlaceholder, redacted.GetProperty("applicationSecret").GetString());
    }

    [Theory]
    [InlineData("clientSecret")]
    [InlineData("Password")]
    [InlineData("certificateThumbprint")]
    [InlineData("apiKey")]
    public void Redact_ReplacesConventionallySecretNamesWithoutDeclaration(string name)
    {
        var payload = new Dictionary<string, object> { [name] = "super-secret-value" };
        var parameters = JsonSerializer.SerializeToElement(payload);

        var redacted = AuditRedactor.Redact(parameters, redactParams: null);

        Assert.Equal(AuditRedactor.RedactedPlaceholder, redacted.GetProperty(name).GetString());
    }

    [Fact]
    public void Redact_KeepsCredentialRefBecauseItIsANameNotMaterial()
    {
        var parameters = JsonSerializer.SerializeToElement(new { credentialRef = "contoso-prod" });

        var redacted = AuditRedactor.Redact(parameters, redactParams: null);

        Assert.Equal("contoso-prod", redacted.GetProperty("credentialRef").GetString());
    }

    [Fact]
    public void Redact_ScrubsCertificateThumbprintsFoundInInnocentlyNamedValues()
    {
        var parameters = JsonSerializer.SerializeToElement(new
        {
            note = "connect with A1B2C3D4E5F60718293A4B5C6D7E8F9012345678 please",
        });

        var redacted = AuditRedactor.Redact(parameters, redactParams: null);

        var note = redacted.GetProperty("note").GetString();
        Assert.DoesNotContain("A1B2C3D4E5F60718293A4B5C6D7E8F9012345678", note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(AuditRedactor.RedactedPlaceholder, note);
    }

    [Fact]
    public void Redact_ScrubsPemPrivateKeyMaterial()
    {
        var parameters = JsonSerializer.SerializeToElement(new
        {
            blob = "-----BEGIN RSA PRIVATE KEY-----\nMIIEow==\n-----END RSA PRIVATE KEY-----",
        });

        var redacted = AuditRedactor.Redact(parameters, redactParams: null);

        Assert.Equal(AuditRedactor.RedactedPlaceholder, redacted.GetProperty("blob").GetString());
    }

    [Fact]
    public void Redact_ScrubsJwtsAnywhereInThePayload()
    {
        const string Jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        var parameters = JsonSerializer.SerializeToElement(new { headers = new[] { $"Authorization: Bearer {Jwt}" } });

        var redacted = AuditRedactor.Redact(parameters, redactParams: null);

        var header = redacted.GetProperty("headers")[0].GetString();
        Assert.DoesNotContain(Jwt, header, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_WalksNestedObjectsAndArrays()
    {
        var parameters = JsonSerializer.SerializeToElement(new
        {
            targets = new[]
            {
                new { upn = "a@contoso.com", clientSecret = "leak-me" },
                new { upn = "b@contoso.com", clientSecret = "leak-me-too" },
            },
        });

        var redacted = AuditRedactor.Redact(parameters, redactParams: null);

        foreach (var target in redacted.GetProperty("targets").EnumerateArray())
        {
            Assert.Equal(AuditRedactor.RedactedPlaceholder, target.GetProperty("clientSecret").GetString());
            Assert.Contains("@contoso.com", target.GetProperty("upn").GetString());
        }
    }

    [Fact]
    public void Redact_PreservesNonStringValues()
    {
        var parameters = JsonSerializer.SerializeToElement(new { pageSize = 25, enabled = true, missing = (string?)null });

        var redacted = AuditRedactor.Redact(parameters, redactParams: null);

        Assert.Equal(25, redacted.GetProperty("pageSize").GetInt32());
        Assert.True(redacted.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, redacted.GetProperty("missing").ValueKind);
    }

    [Fact]
    public void ScrubText_LeavesOrdinaryMessagesIntact()
    {
        const string Message = "The user user@contoso.com was not found.";

        Assert.Equal(Message, AuditRedactor.ScrubText(Message));
    }
}
