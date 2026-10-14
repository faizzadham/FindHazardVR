namespace Convai.Shared.Interfaces
{
    /// <summary>Optionally provides the versioned request reference independently of stable character identity.</summary>
    public interface ICharacterVersionSource
    {
        /// <summary>The validated live reference or exact API-provided released reference to use for requests.</summary>
        string CharacterReference { get; }
    }
}
