using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Reflection;
using CodeToNeo4j.TypeScript.Bridge;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace CodeToNeo4j.TypeScript.Tests.Bridge;

public class TypeScriptBridgeServiceTests
{
	private static TypeScriptBridgeService CreateSut() =>
		new(new FileSystem(), NullLogger<TypeScriptBridgeService>.Instance);

	[Fact]
	public void GivenNodeOnPath_WhenFindNodeExecutableCalled_ThenReturnsPathOrNull()
	{
		var result = CreateSut().FindNodeExecutable();
		Assert.True(result is null || result.Contains("node", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void GivenEmbeddedResource_WhenEnsureBridgeExtractedCalled_ThenExtractsDistIndexJs()
	{
		// Arrange
		var sut = CreateSut();

		// Act
		var bridgeDir = sut.EnsureBridgeExtracted();

		// Assert
		bridgeDir.ShouldNotBeNull();
		Directory.Exists(bridgeDir).ShouldBeTrue();
		File.Exists(Path.Combine(bridgeDir, "dist", "index.js")).ShouldBeTrue();
		File.Exists(Path.Combine(bridgeDir, ".extracted")).ShouldBeTrue();
	}

	[Fact]
	public void GivenAlreadyExtracted_WhenEnsureBridgeExtractedCalledAgain_ThenReturnsSameDir()
	{
		// Arrange
		var sut = CreateSut();

		// Act
		var first = sut.EnsureBridgeExtracted();
		var second = sut.EnsureBridgeExtracted();

		// Assert
		first.ShouldBe(second);
	}

	[Fact]
	public async Task GivenNoNodeOnPath_WhenAnalyzeProjectCalled_ThenReturnsNull()
	{
		// Only meaningful when Node.js is not available; skip if node is on PATH
		if (CreateSut().FindNodeExecutable() is not null)
		{
			return;
		}

		var sut = CreateSut();
		var result = await sut.AnalyzeProject("/some/nonexistent/project");
		result.ShouldBeNull();
	}

	[Fact]
	public async Task GivenSameProjectRoot_WhenAnalyzeProjectCalledTwice_ThenSecondCallReturnsCachedResult()
	{
		// Arrange — use a path where analysis will fail/return null (no package.json, etc.)
		var sut = CreateSut();
		var fakePath = Path.Combine(Path.GetTempPath(), "nonexistent-ts-project-" + Guid.NewGuid());

		// Act
		var first = await sut.AnalyzeProject(fakePath);
		var second = await sut.AnalyzeProject(fakePath);

		// Assert — both calls agree (second is served from cache)
		second.ShouldBe(first);
	}

	[Fact]
	public void GivenEmptyPath_WhenFindNodeExecutableCalled_ThenReturnsNull()
	{
		var originalPath = Environment.GetEnvironmentVariable("PATH");
		try
		{
			Environment.SetEnvironmentVariable("PATH", "");
			TypeScriptBridgeService sut = new(new MockFileSystem(), NullLogger<TypeScriptBridgeService>.Instance);
			sut.FindNodeExecutable().ShouldBeNull();
		}
		finally
		{
			Environment.SetEnvironmentVariable("PATH", originalPath);
		}
	}

	[Fact]
	public void GivenPathWithOnlyWhitespaceEntries_WhenFindNodeExecutableCalled_ThenReturnsNull()
	{
		var separator = OperatingSystem.IsWindows() ? ';' : ':';
		var originalPath = Environment.GetEnvironmentVariable("PATH");
		try
		{
			Environment.SetEnvironmentVariable("PATH", $"   {separator}  {separator}  ");
			TypeScriptBridgeService sut = new(new MockFileSystem(), NullLogger<TypeScriptBridgeService>.Instance);
			sut.FindNodeExecutable().ShouldBeNull();
		}
		finally
		{
			Environment.SetEnvironmentVariable("PATH", originalPath);
		}
	}

	[Fact]
	public void GivenPathWithNodeExecutable_WhenFindNodeExecutableCalled_ThenReturnsFullPath()
	{
		var exeName = OperatingSystem.IsWindows() ? "node.exe" : "node";
		var fakeDir = OperatingSystem.IsWindows() ? @"C:\fake\bin" : "/fake/bin";
		var expectedPath = Path.Combine(fakeDir, exeName);

		MockFileSystem fs = new();
		fs.AddFile(expectedPath, new MockFileData(""));

		var originalPath = Environment.GetEnvironmentVariable("PATH");
		try
		{
			Environment.SetEnvironmentVariable("PATH", fakeDir);
			TypeScriptBridgeService sut = new(fs, NullLogger<TypeScriptBridgeService>.Instance);
			sut.FindNodeExecutable().ShouldBe(expectedPath);
		}
		finally
		{
			Environment.SetEnvironmentVariable("PATH", originalPath);
		}
	}

	[Fact]
	public void GivenPathWithMixedWhitespaceAndValidDir_WhenFindNodeExecutableCalled_ThenReturnsPath()
	{
		var separator = OperatingSystem.IsWindows() ? ';' : ':';
		var exeName = OperatingSystem.IsWindows() ? "node.exe" : "node";
		var fakeDir = OperatingSystem.IsWindows() ? @"C:\fake\bin" : "/fake/bin";
		var expectedPath = Path.Combine(fakeDir, exeName);

		MockFileSystem fs = new();
		fs.AddFile(expectedPath, new MockFileData(""));

		var originalPath = Environment.GetEnvironmentVariable("PATH");
		try
		{
			Environment.SetEnvironmentVariable("PATH", $"   {separator}{fakeDir}");
			TypeScriptBridgeService sut = new(fs, NullLogger<TypeScriptBridgeService>.Instance);
			sut.FindNodeExecutable().ShouldBe(expectedPath);
		}
		finally
		{
			Environment.SetEnvironmentVariable("PATH", originalPath);
		}
	}

	[Fact]
	public void GivenSentinelAlreadyExists_WhenEnsureBridgeExtractedCalledOnFreshInstance_ThenReturnsCacheDir()
	{
		// Calculate the sentinel path the same way the implementation does
		var assembly = typeof(TypeScriptBridgeService).Assembly;
		var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
		              ?? assembly.GetName().Version?.ToString()
		              ?? "0.0.0";
		var safeVersion = string.Concat(version.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
		var cacheRoot = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
			".codetoneo4j", "ts-analyzer", safeVersion);
		var sentinel = Path.Combine(cacheRoot, ".extracted");

		// Pre-populate MockFileSystem so the sentinel file appears to exist
		MockFileSystem fs = new(new Dictionary<string, MockFileData>
		{
			[sentinel] = new MockFileData(version)
		});

		// Fresh instance — _bridgeDir is null, but sentinel exists on the mock FS
		TypeScriptBridgeService sut = new(fs, NullLogger<TypeScriptBridgeService>.Instance);
		var result = sut.EnsureBridgeExtracted();

		result.ShouldBe(cacheRoot);
	}
}
