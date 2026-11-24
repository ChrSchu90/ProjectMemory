namespace ProjectMemory.Routing;

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

/// <summary>
/// Represents a canonical technical context identifier used for project routing.
/// </summary>
[DebuggerDisplay("ContextId: {BindingName} ({BindingType})")]
public sealed record ContextId
{
    #region Static Fields

    /// <summary>
    /// The ChatGPT project identifier
    /// </summary>
    internal const string ChatProjectIdentifier = "chat";

    /// <summary>
    /// The Git repository identifier
    /// </summary>
    internal const string GitRepositoryIdentifier = "git";

    /// <summary>
    /// Identifies an explicitly configured workspace without Git remotes.
    /// </summary>
    internal const string LocalRepositoryIdentifier = "local";

    #endregion

    #region Private Fields

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ContextId"/> class.
    /// </summary>
    /// <param name="bindingType">Type of the binding.</param>
    /// <param name="bindingName">Name of the binding.</param>
    private ContextId(BindingType bindingType, string bindingName)
    {
        BindingType = bindingType;
        BindingName = bindingName;
    }

    #endregion

    #region Properties

    /// <summary>
    /// Gets the binding type.
    /// </summary>
    public BindingType BindingType { get; }

    /// <summary>
    /// Gets the opaque binding-specific identifier.
    /// </summary>
    public string BindingName { get; }

    #endregion

    #region Public Methods

    /// <summary>
    /// Creates a canonical technical context identifier from its persisted binding components.
    /// </summary>
    /// <param name="bindingType">The binding type.</param>
    /// <param name="bindingName">The opaque binding-specific identifier.</param>
    /// <returns>The canonical context identifier.</returns>
    public static ContextId Create(BindingType bindingType, string bindingName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingName);

        var value = $"{GetBindingTypeIdentifier(bindingType)}:{bindingName}";
        if (!TryParse(value, out var contextId))
            throw new ArgumentException("The binding name does not form a valid canonical context identifier.", nameof(bindingName));

        return contextId;
    }

    /// <summary>
    /// Attempts to parse a canonical technical context identifier.
    /// </summary>
    /// <param name="value">The context identifier.</param>
    /// <param name="contextId">The parsed context identifier when successful.</param>
    /// <returns><see langword="true"/> when the identifier is valid; otherwise <see langword="false"/>.</returns>
    public static bool TryParse(string? value, [NotNullWhen(true)] out ContextId? contextId)
    {
        contextId = null;

        if (string.IsNullOrEmpty(value))
            return false;

        if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
            return false;

        var separatorIndex = value.IndexOf(':');
        if (separatorIndex <= 0 || separatorIndex == value.Length - 1)
            return false;

        var typeIdentifier = value[..separatorIndex];
        var bindingName = value[(separatorIndex + 1)..];

        if (bindingName.Length > 255 || bindingName.Any(c => c < '!' || c > '~'))
            return false;

        if (!TryParseBindingType(typeIdentifier, out var bindingType))
            return false;

        contextId = new ContextId(bindingType, bindingName);
        return true;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return $"{GetBindingTypeIdentifier(BindingType)}:{BindingName}";
    }

    #endregion

    #region Private Methods

    private static bool TryParseBindingType(string identifier, out BindingType bindingType)
    {
        switch (identifier)
        {
            case ChatProjectIdentifier:
                bindingType = BindingType.ChatProject;
                return true;
            case GitRepositoryIdentifier:
                bindingType = BindingType.GitRepository;
                return true;
            case LocalRepositoryIdentifier:
                bindingType = BindingType.LocalRepository;
                return true;
            default:
                bindingType = default;
                return false;
        }
    }

    private static string GetBindingTypeIdentifier(BindingType bindingType)
    {
        return bindingType switch
        {
            BindingType.ChatProject => ChatProjectIdentifier,
            BindingType.GitRepository => GitRepositoryIdentifier,
            BindingType.LocalRepository => LocalRepositoryIdentifier,
            _ => throw new ArgumentOutOfRangeException(nameof(bindingType), bindingType, null)
        };
    }

    #endregion
}
