using System.Text;
using MicroFx.Storage;
using Microsoft.Extensions.Options;

namespace MicroFx.Tests.Storage;

[TestFixture]
internal sealed class FileSystemObjectStoreTests
{
    private string _root = null!;
    private IObjectStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "microfx-tests-" + Guid.NewGuid().ToString("N")[..8]);
        _store = new FileSystemObjectStore(
            Options.Create(new StorageOptions { RootPath = _root, MaximumObjectBytes = 1024 }));
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ---- Path traversal ----------------------------------------------------------------------

    [TestCase("../escape")]
    [TestCase("../../etc/passwd")]
    [TestCase("nested/../../escape")]
    [TestCase("./relative")]
    [TestCase("a/./b")]
    public void Relative_segments_are_rejected(string key) =>
        Assert.ThrowsAsync<ArgumentException>(async () =>
            await _store.PutAsync(key, new MemoryStream([1])));

    [TestCase("with space")]
    [TestCase("semi;colon")]
    [TestCase("null\0byte")]
    [TestCase("back\\slash")]
    [TestCase("tilde~expand")]
    [TestCase("C:/absolute")]
    public void Keys_outside_the_permitted_alphabet_are_rejected(string key) =>
        Assert.ThrowsAsync<ArgumentException>(async () =>
            await _store.PutAsync(key, new MemoryStream([1])));

    [Test]
    public void An_over_long_key_is_rejected() =>
        Assert.ThrowsAsync<ArgumentException>(async () =>
            await _store.PutAsync(new string('a', 1025), new MemoryStream([1])));

    [Test]
    public async Task Nothing_is_written_outside_the_root()
    {
        try
        {
            await _store.PutAsync("../escaped.txt", new MemoryStream([1]));
        }
        catch (ArgumentException)
        {
            // expected
        }

        var parent = Directory.GetParent(_root)!.FullName;
        Assert.That(File.Exists(Path.Combine(parent, "escaped.txt")), Is.False);
    }

    // ---- Round trip ---------------------------------------------------------------------------

    [Test]
    public async Task An_object_round_trips()
    {
        var payload = Encoding.UTF8.GetBytes("hello world");
        await _store.PutAsync("folder/object.txt", new MemoryStream(payload));

        await using var stream = await _store.GetAsync("folder/object.txt");
        using var reader = new StreamReader(stream!);

        Assert.That(await reader.ReadToEndAsync(), Is.EqualTo("hello world"));
    }

    [Test]
    public async Task An_absent_object_reads_as_null() =>
        Assert.That(await _store.GetAsync("never/written"), Is.Null);

    [Test]
    public async Task Stat_reports_size()
    {
        await _store.PutAsync("sized", new MemoryStream(new byte[42]));

        var info = await _store.StatAsync("sized");

        Assert.That(info!.Value.SizeBytes, Is.EqualTo(42));
    }

    [Test]
    public async Task Delete_is_idempotent()
    {
        await _store.PutAsync("transient", new MemoryStream([1]));
        await _store.DeleteAsync("transient");
        await _store.DeleteAsync("transient");

        Assert.That(await _store.StatAsync("transient"), Is.Null);
    }

    // ---- Size limit ---------------------------------------------------------------------------

    [Test]
    public void An_oversized_object_is_rejected() =>
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _store.PutAsync("too-big", new MemoryStream(new byte[2048])));

    [Test]
    public async Task A_rejected_write_leaves_no_partial_object()
    {
        // The temp-file-then-move pattern exists so a failed write cannot leave a truncated object
        // that a reader would treat as complete.
        try
        {
            await _store.PutAsync("too-big", new MemoryStream(new byte[2048]));
        }
        catch (InvalidOperationException)
        {
            // expected
        }

        Assert.Multiple(async () =>
        {
            Assert.That(await _store.StatAsync("too-big"), Is.Null);
            Assert.That(Directory.GetFiles(_root, "*.tmp-*", SearchOption.AllDirectories), Is.Empty);
        });
    }
}
