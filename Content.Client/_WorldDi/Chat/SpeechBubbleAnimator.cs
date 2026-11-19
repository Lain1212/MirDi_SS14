// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;
using Content.Shared._WorldDi;
using Content.Shared.Chat;
using Content.Shared.Speech;
using Robust.Client.Audio;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Audio;
using Robust.Shared.Configuration;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using SpeechType = Content.Client.Chat.UI.SpeechBubble.SpeechType;

namespace Content.Client._WorldDi.Chat;

public sealed class SpeechBubbleAnimator
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IEntityManager _entityManager = default!;
    [Dependency] private readonly IConfigurationManager _configManager = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly IRobustRandom _random = default!;

    private readonly AudioSystem _audioSystem;
    private readonly EntityUid _senderEntity;
    private readonly SpeechType _speechType;
    private readonly string _speechStyleClass;
    private readonly Color? _fontColor;
    private readonly string _rawContentMarkup;
    private readonly bool _isOoc;

    private RichTextLabel? _speechLabel;
    private string _cachedRawString = string.Empty;
    private int _totalTextLength;
    private int _visibleCharactersCount;
    private float _accumulatedTime;
    private float _nextCharacterDelay = 0.05f;
    private bool _isBarkFinished;

    private static readonly Dictionary<EntityUid, int> ActiveBarksCount = new();

    public SpeechBubbleAnimator(
        SpeechType type,
        ChatMessage message,
        EntityUid senderEntity,
        string speechStyleClass,
        Color? fontColor)
    {
        IoCManager.InjectDependencies(this);
        _audioSystem = _entityManager.System<AudioSystem>();

        _speechType = type;
        _senderEntity = senderEntity;
        _speechStyleClass = speechStyleClass;
        _fontColor = fontColor;
        _isOoc = type == SpeechType.Looc;

        var fancyContent = SharedChatSystem.GetStringInsideTag(message, "BubbleContent");
        _rawContentMarkup = !string.IsNullOrEmpty(fancyContent) ? fancyContent : message.WrappedMessage;

        if (ShouldTrackBarks())
        {
            ActiveBarksCount.TryGetValue(_senderEntity, out var count);
            ActiveBarksCount[_senderEntity] = count + 1;
        }
    }

    public void AttachLabel(RichTextLabel label)
    {
        _speechLabel = label;
        _cachedRawString = label.GetFormattedMessage()?.ToString() ?? string.Empty;
        _totalTextLength = _cachedRawString.Length;
    }

    public void OnBubbleDied()
    {
        FinishBarkTracking();
    }

    public void Update(float deltaSeconds, ref TimeSpan deathTime, Func<string, Color?, FormattedMessage> formatSpeech)
    {
        if (_speechLabel == null || _visibleCharactersCount >= _totalTextLength)
            return;

        if (_speechStyleClass == "emoteBox")
        {
            _visibleCharactersCount = _totalTextLength;
            _speechLabel.SetMessage(formatSpeech(_rawContentMarkup, _fontColor));
            return;
        }

        if (!_configManager.GetCVar(CCVars.ChatTextAnimationEnabled))
        {
            _visibleCharactersCount = _totalTextLength;
            _speechLabel.SetMessage(formatSpeech(_rawContentMarkup, _fontColor));
            PlayInstantBarks();
            FinishBarkTracking();
            return;
        }

        _accumulatedTime += deltaSeconds;
        if (_accumulatedTime < _nextCharacterDelay)
            return;

        var charactersToAdd = (int)(_accumulatedTime / _nextCharacterDelay);
        _accumulatedTime -= charactersToAdd * _nextCharacterDelay;

        var previousCount = _visibleCharactersCount;
        _visibleCharactersCount = Math.Min(_totalTextLength, _visibleCharactersCount + charactersToAdd);

        if (_visibleCharactersCount != previousCount)
        {
            var lastChar = _cachedRawString[_visibleCharactersCount - 1];
            char? nextChar = _cachedRawString.Length > _visibleCharactersCount
                ? _cachedRawString[_visibleCharactersCount]
                : null;

            var (delaySeconds, shouldPlaySound) = AnalyzeCharacter(lastChar, nextChar);
            _nextCharacterDelay = delaySeconds;

            if (shouldPlaySound && !IsSpamming())
                PlaySingleBark();

            var invisibleTailMarkup = BuildTransparencyMarkup(_rawContentMarkup, _visibleCharactersCount);
            _speechLabel.SetMessage(formatSpeech(invisibleTailMarkup, _fontColor));
        }

        var timeLeft = (float)(deathTime - _timing.RealTime).TotalSeconds;
        deathTime = _timing.RealTime + TimeSpan.FromSeconds(MathF.Max(timeLeft, 2.0f));

        if (_visibleCharactersCount >= _totalTextLength)
            FinishBarkTracking();
    }

    private void PlayInstantBarks()
    {
        if (_isOoc || IsSpamming() || !_configManager.GetCVar(CCVars.ChatVoiceBarksEnabled))
            return;

        if (!TryGetVoiceData(out var voice, out var barkComp) || voice.Sounds.Count == 0)
            return;

        var calculatedBarks = 0;
        var accumulatedDelay = 0f;

        for (var i = 0; i < _cachedRawString.Length; i++)
        {
            if (calculatedBarks >= 12)
                break;

            var lastChar = _cachedRawString[i];
            char? nextChar = _cachedRawString.Length > i + 1 ? _cachedRawString[i + 1] : null;
            var (delaySeconds, shouldPlaySound) = AnalyzeCharacter(lastChar, nextChar);

            if (shouldPlaySound)
            {
                calculatedBarks++;
                if (accumulatedDelay == 0f)
                {
                    PlayBarkSound(voice, barkComp);
                }
                else
                {
                    Timer.Spawn(TimeSpan.FromSeconds(accumulatedDelay), () =>
                    {
                        if (!_entityManager.Deleted(_senderEntity) && TryGetVoiceData(out var v, out var b))
                            PlayBarkSound(v, b);
                    });
                }
            }

            accumulatedDelay += delaySeconds;
        }
    }

    private void PlaySingleBark()
    {
        if (_isOoc || !_configManager.GetCVar(CCVars.ChatVoiceBarksEnabled))
            return;

        if (TryGetVoiceData(out var voice, out var barkComp) && voice.Sounds.Count > 0)
            PlayBarkSound(voice, barkComp);
    }

    private void PlayBarkSound(VoicePrototype voice, VoiceBarkComponent? barkComp)
    {
        var randomSound = _random.Pick(voice.Sounds);
        var basePitch = barkComp?.BasePitch ?? voice.BasePitch;
        var pitchVar = barkComp?.PitchVariation ?? voice.PitchVariation;
        var randomPitch = Math.Max(0.2f, basePitch + _random.NextFloat(-pitchVar, pitchVar));

        var volume = voice.Volume;
        if (_speechType == SpeechType.Whisper)
            volume -= 4f;

        var audioParams = new AudioParams { Pitch = randomPitch, Volume = volume };
        _audioSystem.PlayLocal(randomSound, _senderEntity, null, audioParams);
    }

    private bool TryGetVoiceData(out VoicePrototype voice, out VoiceBarkComponent? barkComp)
    {
        voice = default!;
        if (_entityManager.TryGetComponent(_senderEntity, out barkComp) && barkComp.VoiceId != null)
        {
            if (_prototypeManager.TryIndex<VoicePrototype>(barkComp.VoiceId, out var indexed))
            {
                voice = indexed;
                return true;
            }
        }

        if (_prototypeManager.TryIndex<VoicePrototype>("DefaultVoice", out var defaultVoice))
        {
            voice = defaultVoice;
            return true;
        }

        return false;
    }

    private bool ShouldTrackBarks() => !_isOoc && _speechStyleClass != "emoteBox";

    private bool IsSpamming() => ActiveBarksCount.TryGetValue(_senderEntity, out var count) && count > 1;

    private void FinishBarkTracking()
    {
        if (_isBarkFinished || !ShouldTrackBarks())
            return;

        _isBarkFinished = true;
        if (ActiveBarksCount.TryGetValue(_senderEntity, out var count))
        {
            if (count <= 1)
                ActiveBarksCount.Remove(_senderEntity);
            else
                ActiveBarksCount[_senderEntity] = count - 1;
        }
    }

    private static (float delaySeconds, bool playSound) AnalyzeCharacter(char current, char? next)
    {
        var isFollowedBySpaceOrEnd = !next.HasValue || next.Value == ' ';

        if (current == '.' && isFollowedBySpaceOrEnd) return (0.6f, false);
        if ((current == '!' || current == '?' || current == ';') && isFollowedBySpaceOrEnd) return (0.4f, false);
        if (current == ',' && isFollowedBySpaceOrEnd) return (0.25f, false);
        if (current == ' ') return (0.03f, false);

        return (0.03f, true);
    }

    private static string BuildTransparencyMarkup(string markup, int visibleCharsLimit)
    {
        if (string.IsNullOrEmpty(markup))
            return string.Empty;

        var result = new StringBuilder();
        var currentVisibleCount = 0;
        var insideTag = false;
        var transparencyOpened = false;

        for (var i = 0; i < markup.Length; i++)
        {
            var c = markup[i];
            if (c == '[') insideTag = true;

            if (!insideTag && currentVisibleCount >= visibleCharsLimit && !transparencyOpened)
            {
                result.Append("[color=#00000000]");
                transparencyOpened = true;
            }

            result.Append(c);

            if (!insideTag && !transparencyOpened)
                currentVisibleCount++;

            if (c == ']') insideTag = false;
        }

        if (transparencyOpened)
            result.Append("[/color]");

        return result.ToString();
    }
}
