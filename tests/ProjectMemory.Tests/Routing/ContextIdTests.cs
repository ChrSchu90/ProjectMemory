namespace ProjectMemory.Tests.Routing;

using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjectMemory.Routing;

/// <summary>
/// Tests for <see cref="ContextId"/>
/// </summary>
[TestClass]
public sealed class ContextIdTests
{
    #region Static Fields

    #endregion

    #region Private Fields

    #endregion

    #region Test Constructors

    #endregion

    #region Test Preparation

    #endregion

    #region Tests

    /// <summary>
    /// Verifies that binding components are formatted into the canonical technical context identifier.
    /// </summary>
    /// <param name="bindingType">The supported namespace represented by the binding.</param>
    /// <param name="bindingName">The exact case-sensitive suffix to preserve in the context ID.</param>
    /// <param name="expected">The complete context ID expected after formatting the supplied binding components.</param>
    [TestMethod]
    [DataRow(BindingType.ChatProject, "chatty-mcp-and-aiharborvm", "chat:chatty-mcp-and-aiharborvm")]
    [DataRow(BindingType.GitRepository, "github.com/ChrSchu90/ProjectMemory", "git:github.com/ChrSchu90/ProjectMemory")]
    public void CreateReturnsCanonicalContextId(BindingType bindingType, string bindingName, string expected)
    {
        var contextId = ContextId.Create(bindingType, bindingName);

        Assert.AreEqual(bindingType, contextId.BindingType);
        Assert.AreEqual(bindingName, contextId.BindingName);
        Assert.AreEqual(expected, contextId.ToString());
    }

    /// <summary>
    /// Verifies that invalid binding names are rejected when creating a canonical context identifier.
    /// </summary>
    /// <param name="bindingName">The invalid or padded suffix that context creation must reject.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow(" repository")]
    [DataRow("repository ")]
    public void CreateRejectsInvalidBindingNames(string bindingName)
    {
        Assert.ThrowsExactly<ArgumentException>(() => ContextId.Create(BindingType.GitRepository, bindingName));
    }

    /// <summary>
    /// Verifies that a canonical ChatGPT project context is parsed into its expected type and name.
    /// </summary>
    [TestMethod]
    public void TryParseParsesChatGptProjectContext()
    {
        var success = ContextId.TryParse("chat:chatty-mcp-and-aiharborvm", out var contextId);

        Assert.IsTrue(success);
        Assert.IsNotNull(contextId);
        Assert.AreEqual(BindingType.ChatProject, contextId.BindingType);
        Assert.AreEqual("chatty-mcp-and-aiharborvm", contextId.BindingName);
    }

    /// <summary>
    /// Verifies that a Git repository context parses into its type and name and formats back to the original identifier.
    /// </summary>
    [TestMethod]
    public void TryParseParsesGitRepositoryContext()
    {
        const string value = "git:github.com/ChrSchu90/ProjectMemory";
        var success = ContextId.TryParse(value, out var contextId);

        Assert.IsTrue(success);
        Assert.IsNotNull(contextId);
        Assert.AreEqual(BindingType.GitRepository, contextId.BindingType);
        Assert.AreEqual("github.com/ChrSchu90/ProjectMemory", contextId.BindingName);
        Assert.AreEqual(value, contextId.ToString());
    }

    /// <summary>
    /// Verifies that only the first colon separates the context type from the opaque binding name.
    /// </summary>
    [TestMethod]
    public void TryParsePreservesColonInsideBindingName()
    {
        var success = ContextId.TryParse("git:git.example.com:2222/owner/repository", out var contextId);

        Assert.IsTrue(success);
        Assert.IsNotNull(contextId);
        Assert.AreEqual("git.example.com:2222/owner/repository", contextId.BindingName);
    }

    /// <summary>
    /// Verifies that invalid or non-canonical context identifiers are rejected.
    /// </summary>
    /// <param name="value">The invalid context ID, including null, unsupported prefixes or malformed suffixes.</param>
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("git:")]
    [DataRow(":repository")]
    [DataRow("unknown:value")]
    [DataRow("local:")]
    [DataRow("CHAT:value")]
    [DataRow("LOCAL:value")]
    [DataRow("chat:has space")]
    [DataRow("chat:has\ttab")]
    [DataRow("local:line\nbreak")]
    [DataRow("git:non-ascii-ü")]
    [DataRow("chat:delete-\u007f")]
    [DataRow("GIT:github.com/owner/repository")]
    [DataRow(" git:github.com/owner/repository")]
    [DataRow("git:github.com/owner/repository ")]
    [DataRow("git: github.com/owner/repository")]
    public void TryParseRejectsInvalidContextIds(string? value)
    {
        var success = ContextId.TryParse(value, out var contextId);

        Assert.IsFalse(success);
        Assert.IsNull(contextId);
    }

    /// <summary>
    /// Verifies that all three namespaces preserve case and accept exactly the approved suffix boundaries.
    /// </summary>
    /// <param name="prefix">The supported context namespace whose case sensitivity and suffix boundaries are verified.</param>
    [TestMethod]
    [DataRow("git:")]
    [DataRow("chat:")]
    [DataRow("local:")]
    public void ExactCaseAndSuffixBoundaries(string prefix)
    {
        Assert.IsTrue(ContextId.TryParse(prefix + "Portal", out var upper));
        Assert.IsTrue(ContextId.TryParse(prefix + "portal", out var lower));
        Assert.AreNotEqual(upper, lower);
        Assert.AreEqual(prefix + "Portal", upper.ToString());
        Assert.IsTrue(ContextId.TryParse(prefix + "A", out _));
        Assert.IsTrue(ContextId.TryParse(prefix + new string('A', 255), out _));
        Assert.IsFalse(ContextId.TryParse(prefix + new string('A', 256), out _));
    }

    #endregion

    #region Private Methods

    #endregion

    #region Test Classes

    #endregion
}
