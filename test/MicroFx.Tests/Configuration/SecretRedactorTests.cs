using MicroFx.Configuration;

namespace MicroFx.Tests.Configuration;

[TestFixture]
internal sealed class SecretRedactorTests
{
    [TestCase("Database:Password")]
    [TestCase("Auth:ClientSecret")]
    [TestCase("MicroFx:Messaging:RabbitMq:Pwd")]
    [TestCase("Api__Token")]
    [TestCase("Storage:AccessKey")]
    [TestCase("ConnectionStrings:Orders")]
    [TestCase("Signing:PrivateKey")]
    [TestCase("Some:APIKEY")]
    public void Sensitive_keys_are_redacted(string key) =>
        Assert.That(SecretRedactor.Redact(key, "s3cret"), Is.EqualTo(SecretRedactor.Redacted));

    [TestCase("MicroFx:Host:ManagementPort")]
    [TestCase("Logging:LogLevel:Default")]
    [TestCase("MicroFx:Observability:SampleRatio")]
    public void Ordinary_keys_are_shown(string key) =>
        Assert.That(SecretRedactor.Redact(key, "8081"), Is.EqualTo("8081"));

    [Test]
    public void A_value_shaped_like_a_connection_string_is_redacted_whatever_its_key_is_called()
    {
        // Shape matters as well as name: credentials embedded in a value under an innocuous key
        // would otherwise sail straight through.
        var value = "Host=db;Username=app;Password=hunter2";

        Assert.That(SecretRedactor.Redact("Orders:Primary", value),
            Is.EqualTo(SecretRedactor.Redacted));
    }

    [Test]
    public void Null_values_stay_null() =>
        Assert.That(SecretRedactor.Redact("Anything", null), Is.Null);

    [Test]
    public void Matching_is_case_insensitive() =>
        Assert.That(SecretRedactor.IsSensitive("SOME:PASSWORD"), Is.True);
}
