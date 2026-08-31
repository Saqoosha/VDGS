using System;
using System.IO;
using Xunit;

namespace VDGS.Tests
{
    public class VdgsPathsTests
    {
        [Theory]
        [InlineData("ui")]
        [InlineData("UI")]
        [InlineData("Ui")]
        public void Ui_is_reserved(string name)
        {
            Assert.True(VdgsPaths.IsReservedSceneName(name));
        }

        [Theory]
        [InlineData("playroom")]
        [InlineData("ui-extra")]
        [InlineData("")]
        public void Other_names_are_scenes(string name)
        {
            Assert.False(VdgsPaths.IsReservedSceneName(name));
        }
    }

    public class VdgsPathsResolveTests : IDisposable
    {
        private readonly string _root;

        public VdgsPathsResolveTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "vdgs-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "assets"));
            File.WriteAllText(Path.Combine(_root, "index.html"), "<!doctype html>");
            File.WriteAllText(Path.Combine(_root, "assets", "app.js"), "1");
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        [Fact]
        public void Root_serves_index()
        {
            var r = VdgsPaths.ResolveUi(_root, "/", out var p);
            Assert.Equal(VdgsPaths.UiResult.File, r);
            Assert.Equal("index.html", Path.GetFileName(p));
        }

        [Fact]
        public void Existing_asset_is_a_file()
        {
            var r = VdgsPaths.ResolveUi(_root, "/assets/app.js", out var p);
            Assert.Equal(VdgsPaths.UiResult.File, r);
            Assert.Equal("app.js", Path.GetFileName(p));
        }

        [Fact]
        public void Library_falls_back_to_spa()
        {
            var r = VdgsPaths.ResolveUi(_root, "/library", out var p);
            Assert.Equal(VdgsPaths.UiResult.Spa, r);
            Assert.Equal("index.html", Path.GetFileName(p));
        }

        [Fact]
        public void Missing_js_is_404_not_spa()
        {
            var r = VdgsPaths.ResolveUi(_root, "/assets/nope.js", out _);
            Assert.Equal(VdgsPaths.UiResult.NotFound, r);
        }

        [Theory]
        [InlineData("/../secret")]
        [InlineData("/assets/../../etc/passwd")]
        [InlineData("/assets\\..\\..\\x")]
        public void Escape_is_forbidden(string url)
        {
            var r = VdgsPaths.ResolveUi(_root, url, out _);
            Assert.Equal(VdgsPaths.UiResult.Forbidden, r);
        }

        [Fact]
        public void Missing_root_is_MissingUi()
        {
            var r = VdgsPaths.ResolveUi(Path.Combine(_root, "nope"), "/", out _);
            Assert.Equal(VdgsPaths.UiResult.MissingUi, r);
        }

        [Fact]
        public void Assets_are_immutable_cache()
        {
            Assert.Contains("immutable", VdgsPaths.CacheControl("/assets/app.js"));
            Assert.Equal("no-store", VdgsPaths.CacheControl("/"));
        }
    }
}
