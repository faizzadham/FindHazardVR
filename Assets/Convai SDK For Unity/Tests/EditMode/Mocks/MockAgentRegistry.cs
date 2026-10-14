using System;
using System.Collections.Generic;
using Convai.Runtime.Behaviors;
using UnityEngine;

namespace Convai.Tests.EditMode.Mocks
{
    /// <summary>
    ///     Lightweight mock for IAgentRegistry used in edit-mode tests.
    /// </summary>
    public sealed class MockAgentRegistry : IAgentRegistry
    {
        private readonly Dictionary<string, AudioSource> _audioSources = new();
        private readonly List<IConvaiCharacterAgent> _characters = new();
        private readonly Dictionary<string, string> _ownership = new();
        private readonly List<IConvaiPlayerAgent> _players = new();
        private readonly Dictionary<string, string> _participantIds = new();
        private readonly Dictionary<string, IConvaiCharacterAgent> _participantToCharacter = new();
        private readonly HashSet<string> _mutedCharacters = new();

        public IReadOnlyList<IConvaiCharacterAgent> Characters => _characters;
        public IReadOnlyList<IConvaiPlayerAgent> Players => _players;
        public IConvaiPlayerAgent LocalPlayer => _players.Count > 0 ? _players[0] : null;

        public event Action<IConvaiCharacterAgent> CharacterRegistered;
        public event Action<IConvaiCharacterAgent> CharacterUnregistered;
        public event Action<IConvaiPlayerAgent> PlayerRegistered;

        public void RegisterCharacter(IConvaiCharacterAgent character, string ownerId = null)
        {
            if (character != null && !_characters.Contains(character))
            {
                _characters.Add(character);
                if (!string.IsNullOrEmpty(ownerId))
                    _ownership[character.CharacterId] = ownerId;
                CharacterRegistered?.Invoke(character);
            }
        }

        public void RegisterPlayer(IConvaiPlayerAgent player)
        {
            if (player != null && !_players.Contains(player))
            {
                _players.Add(player);
                PlayerRegistered?.Invoke(player);
            }
        }

        public void Unregister(IConvaiCharacterAgent character)
        {
            if (character != null)
            {
                _characters.Remove(character);
                _ownership.Remove(character.CharacterId);
                _audioSources.Remove(character.CharacterId);
                CharacterUnregistered?.Invoke(character);
            }
        }

        public void Unregister(IConvaiPlayerAgent player)
        {
            if (player != null)
                _players.Remove(player);
        }

        public bool TryGetCharacter(string characterId, out IConvaiCharacterAgent agent)
        {
            agent = _characters.Find(c => c.CharacterId == characterId);
            return agent != null;
        }

        public string GetOwner(IConvaiCharacterAgent character)
        {
            if (character == null) return null;
            return _ownership.TryGetValue(character.CharacterId, out string ownerId) ? ownerId : null;
        }

        public IReadOnlyList<IConvaiCharacterAgent> GetCharactersByOwner(string ownerId)
        {
            var result = new List<IConvaiCharacterAgent>();
            foreach (var character in _characters)
            {
                if (_ownership.TryGetValue(character.CharacterId, out string charOwnerId))
                {
                    if (charOwnerId == ownerId)
                        result.Add(character);
                }
                else if (string.IsNullOrEmpty(ownerId))
                {
                    // Unowned characters belong to local player (null/empty ownerId)
                    result.Add(character);
                }
            }
            return result;
        }

        public int GetCharacterCountByOwner(string ownerId)
        {
            return GetCharactersByOwner(ownerId).Count;
        }

        public bool TryGetCharacterById(string characterId, out IConvaiCharacterAgent agent)
        {
            return TryGetCharacter(characterId, out agent);
        }

        public bool TryGetAudioSource(string characterId, out AudioSource audioSource)
        {
            return _audioSources.TryGetValue(characterId, out audioSource);
        }

        public void SetAudioSource(string characterId, AudioSource audioSource)
        {
            if (!string.IsNullOrEmpty(characterId))
                _audioSources[characterId] = audioSource;
        }

        #region Transport Binding (ParticipantId Mapping)

        public void SetParticipantId(string characterId, string participantId)
        {
            if (string.IsNullOrEmpty(characterId)) return;

            _participantIds[characterId] = participantId;
            if (TryGetCharacter(characterId, out var agent))
            {
                _participantToCharacter[participantId] = agent;
            }
        }

        public bool TryGetParticipantId(string characterId, out string participantId)
        {
            return _participantIds.TryGetValue(characterId, out participantId);
        }

        public bool TryGetCharacterByParticipantId(string participantId, out IConvaiCharacterAgent agent)
        {
            return _participantToCharacter.TryGetValue(participantId, out agent);
        }

        public void ClearTransportBindings()
        {
            _participantIds.Clear();
            _participantToCharacter.Clear();
            _mutedCharacters.Clear();
        }

        #endregion

        #region Mute State

        public void SetCharacterMuted(string characterId, bool muted)
        {
            if (string.IsNullOrEmpty(characterId)) return;

            if (muted)
                _mutedCharacters.Add(characterId);
            else
                _mutedCharacters.Remove(characterId);
        }

        public bool IsCharacterMuted(string characterId)
        {
            return !string.IsNullOrEmpty(characterId) && _mutedCharacters.Contains(characterId);
        }

        #endregion

        // Test helper methods
        public bool HasCharacter(string characterId) =>
            _characters.Exists(c => c.CharacterId == characterId);

        public bool HasPlayer(string playerId) =>
            _players.Exists(p => p.PlayerId == playerId);

        public void Clear()
        {
            _characters.Clear();
            _players.Clear();
            _ownership.Clear();
            _audioSources.Clear();
        }
    }
}
