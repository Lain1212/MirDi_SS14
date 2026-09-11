// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Client.UserInterface.RichText; // Goob
using System.Numerics;
using Content.Shared.CCVar;
using Content.Shared.Chat;
using Content.Shared.Speech;
using Robust.Client.Audio;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.RichText;
using Robust.Shared.Audio;
using Robust.Shared.Configuration;
using Robust.Shared.IoC;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using System;
using System.Collections.Generic;
using static Content.Client.Chat.UI.SpeechBubble;

namespace Content.Client.Chat.UI
{
    public abstract class SpeechBubble : Control
    {
        [Dependency] private readonly IGameTiming _timing = default!;
        [Dependency] private readonly IEyeManager _eyeManager = default!;
        [Dependency] private readonly IEntityManager _entityManager = default!;
        [Dependency] protected readonly IConfigurationManager ConfigManager = default!;
        [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
        [Dependency] private readonly IRobustRandom _random = default!;
        private readonly SharedTransformSystem _transformSystem;

        // <Goob>
        public static readonly Type[] AllowedTags =
        [
            typeof(BoldItalicTag),
            typeof(BoldTag),
            typeof(BulletTag),
            typeof(ColorTag),
            typeof(HeadingTag),
            typeof(ItalicTag),
            typeof(FontTag),
        ];
        // </Goob>

        public enum SpeechType : byte
        {
            Emote,
            Say,
            Whisper,
            Looc
        }

        private static readonly Dictionary<EntityUid, int> ActiveBarksCount = new();
        /// <summary>
        ///     The total time a speech bubble stays on screen.
        /// </summary>
        private static readonly TimeSpan TotalTime = TimeSpan.FromSeconds(4);

        /// <summary>
        ///     The amount of time at the end of the bubble's life at which it starts fading.
        /// </summary>
        private static readonly TimeSpan FadeTime = TimeSpan.FromSeconds(0.25f);

        /// <summary>
        ///     The distance in world space to offset the speech bubble from the center of the entity.
        ///     i.e. greater -> higher above the mob's head.
        /// </summary>
        private const float EntityVerticalOffset = 0.5f;

        private string _cachedRawString = string.Empty;
        private string _rawContentMarkup = string.Empty;
        private string _speechStyleClass = string.Empty;
        private Color? _languageFontColor;
        /// <summary>
        ///     The default maximum width for speech bubbles.
        /// </summary>
        public const float SpeechMaxWidth = 256;

        private readonly EntityUid _senderEntity;

        /// <summary>
        /// The time at which this bubble will die.
        /// </summary>
        private TimeSpan _deathTime;

        public float VerticalOffset { get; set; }
        private float _verticalOffsetAchieved;

        public Vector2 ContentSize { get; private set; }

        // man down
        public event Action<EntityUid, SpeechBubble>? OnDied;

        protected RichTextLabel? SpeechLabel; 
        private FormattedMessage? _cachedFullMessage;
        private int _totalTextLength = 0;
        private int _visibleCharactersCount = 0;
        private float _accumulatedTime = 0f;
        private float _nextCharacterDelay = 0.05f;
        private VoicePrototype? _voicePrototype;
        private VoiceBarkComponent? _barkComponent;
        private bool _isOoc = false;
        private SpeechType _speechType;
        private bool _isBarkFinished = false;

        public static SpeechBubble CreateSpeechBubble(SpeechType type, ChatMessage message, EntityUid senderEntity)
        {
            switch (type)
            {
                case SpeechType.Emote:
                    return new TextSpeechBubble(type, message, senderEntity, "emoteBox");

                case SpeechType.Say:
                    return new FancyTextSpeechBubble(type, message, senderEntity, "sayBox");

                case SpeechType.Whisper:
                    return new FancyTextSpeechBubble(type, message, senderEntity, "whisperBox");

                case SpeechType.Looc:
                    return new TextSpeechBubble(type, message, senderEntity, "emoteBox", Color.FromHex("#48d1cc"));

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        public SpeechBubble(SpeechType type, ChatMessage message, EntityUid senderEntity, string speechStyleClass, Color? fontColor = null)
        {
            IoCManager.InjectDependencies(this);
            _senderEntity = senderEntity;
            _transformSystem = _entityManager.System<SharedTransformSystem>();
            _speechStyleClass = speechStyleClass;

            // Use text clipping so new messages don't overlap old ones being pushed up.
            RectClipContent = true;

            if (_entityManager.TryGetComponent<VoiceBarkComponent>(_senderEntity, out _barkComponent) && _barkComponent.VoiceId != null)
            {
                _prototypeManager.TryIndex(_barkComponent.VoiceId, out _voicePrototype);
            }
            if (_voicePrototype == null)
            {
                _prototypeManager.TryIndex<VoicePrototype>("DefaultVoice", out _voicePrototype);
            }

            var bubble = BuildBubble(message, speechStyleClass, fontColor);

            AddChild(bubble);

            ForceRunStyleUpdate();

            bubble.Measure(Vector2Helpers.Infinity);
            ContentSize = bubble.DesiredSize;
            _verticalOffsetAchieved = -ContentSize.Y;
            _deathTime = _timing.RealTime + TotalTime;

            if (type == SpeechType.Looc)
            {
                _isOoc = true;
            }

            if (SpeechLabel != null)
            {
                _cachedFullMessage = SpeechLabel.GetFormattedMessage();
                _cachedRawString = _cachedFullMessage?.ToString() ?? string.Empty;
                _totalTextLength = _cachedRawString.Length;
                _speechType = type;
            }

            var fancyContent = SharedChatSystem.GetStringInsideTag(message, "BubbleContent");
            if (!string.IsNullOrEmpty(fancyContent))
            {
                _rawContentMarkup = fancyContent;
            }
            else
            {
                _rawContentMarkup = message.WrappedMessage;
            }

            _languageFontColor = fontColor;
            OnDied += (entity, bubble) =>
            {
                if (!_isBarkFinished && !_isOoc && _speechStyleClass != "emoteBox")
                {
                    if (ActiveBarksCount.TryGetValue(entity, out var count))
                    {
                        if (count <= 1)
                            ActiveBarksCount.Remove(entity);
                        else
                            ActiveBarksCount[entity] = count - 1;
                    }
                }
            };
            if (!_isOoc && _speechStyleClass != "emoteBox")
            {
                if (!ActiveBarksCount.TryGetValue(_senderEntity, out var count))
                    count = 0;

                ActiveBarksCount[_senderEntity] = count + 1;
            }
        }

        protected abstract Control BuildBubble(ChatMessage message, string speechStyleClass, Color? fontColor = null);

        protected override void FrameUpdate(FrameEventArgs args)
        {
            base.FrameUpdate(args);

            var timeLeft = (float)(_deathTime - _timing.RealTime).TotalSeconds;
            if (_entityManager.Deleted(_senderEntity) || timeLeft <= 0)
            {
                // Timer spawn to prevent concurrent modification exception.
                Timer.Spawn(0, Die);
                return;
            }

            if (SpeechLabel != null && _visibleCharactersCount < _totalTextLength)
            {
                if (_speechStyleClass == "emoteBox")
                {
                    _visibleCharactersCount = _totalTextLength;
                    SpeechLabel.SetMessage(FormatSpeech(_rawContentMarkup, _languageFontColor));
                    goto SkipTextUpdate;
                }
                if (!ConfigManager.GetCVar(Shared._WorldDi.CCVars.ChatTextAnimationEnabled))
                {
                    _visibleCharactersCount = _totalTextLength;
                    SpeechLabel.SetMessage(FormatSpeech(_rawContentMarkup, _languageFontColor));
                    bool isSpamming = ActiveBarksCount.TryGetValue(_senderEntity, out var barksCount) && barksCount > 1; // MirDi - спам-защита
                    if (!isSpamming && !_isOoc && _voicePrototype != null && _voicePrototype.Sounds.Count > 0 && ConfigManager.GetCVar(Shared._WorldDi.CCVars.ChatVoiceBarksEnabled))
                    {
                        int calculatedBarks = 0;
                        float accumulatedDelay = 0f;
                        float currentVolume = _voicePrototype.Volume;
                        if (_speechType == SpeechType.Whisper)
                        {
                            currentVolume -= 4f;
                        }

                        for (int i = 0; i < _cachedRawString.Length; i++)
                        {
                            if (calculatedBarks >= 12)
                                break;

                            char lastChar = _cachedRawString[i];
                            char? nextChar = _cachedRawString.Length > i + 1 ? _cachedRawString[i + 1] : null;

                            var (delaySeconds, shouldPlaySound) = AnalyzeCharacter(lastChar, nextChar);

                            if (shouldPlaySound)
                            {
                                calculatedBarks++;

                                var randomSound = _random.Pick(_voicePrototype.Sounds);
                                var basePitch = _barkComponent?.BasePitch ?? _voicePrototype.BasePitch;
                                var pitchVar = _barkComponent?.PitchVariation ?? _voicePrototype.PitchVariation;
                                var randomPitch = Math.Max(0.2f, basePitch + _random.NextFloat(-pitchVar, pitchVar));
                                var audioParams = new AudioParams { Pitch = randomPitch, Volume = currentVolume };
                                if (accumulatedDelay == 0f)
                                {
                                    _entityManager.System<AudioSystem>().PlayLocal(randomSound, _senderEntity, null, audioParams);
                                }
                                else
                                {
                                    Timer.Spawn(TimeSpan.FromSeconds(accumulatedDelay), () =>
                                    {
                                        if (!_entityManager.Deleted(_senderEntity))
                                        {
                                            if (_entityManager.TryGetComponent<VoiceBarkComponent>(_senderEntity, out _barkComponent) && _barkComponent.VoiceId != null)
                                            {
                                                _prototypeManager.TryIndex(_barkComponent.VoiceId, out _voicePrototype);
                                            }

                                            if (_voicePrototype != null && _voicePrototype.Sounds.Count > 0)
                                            {
                                                var randomSound = _random.Pick(_voicePrototype.Sounds);
                                                var basePitch = _barkComponent?.BasePitch ?? _voicePrototype.BasePitch;
                                                var pitchVar = _barkComponent?.PitchVariation ?? _voicePrototype.PitchVariation;
                                                var randomPitch = Math.Max(0.2f, basePitch + _random.NextFloat(-pitchVar, pitchVar));

                                                var audioParams = new AudioParams { Pitch = randomPitch, Volume = currentVolume };
                                                _entityManager.System<AudioSystem>().PlayLocal(randomSound, _senderEntity, null, audioParams);
                                            }
                                        }
                                    });
                                }
                            }
                            accumulatedDelay += delaySeconds;
                        }
                        if (!_isBarkFinished)
                        {
                            _isBarkFinished = true;
                            if (!_isOoc && _speechStyleClass != "emoteBox")
                            {
                                if (ActiveBarksCount.TryGetValue(_senderEntity, out var count))
                                {
                                    if (count <= 1)
                                        ActiveBarksCount.Remove(_senderEntity);
                                    else
                                        ActiveBarksCount[_senderEntity] = count - 1;
                                }
                            }
                        }
                    }
                    goto SkipTextUpdate;
                }

                _accumulatedTime += args.DeltaSeconds;

                if (_accumulatedTime < _nextCharacterDelay)
                    goto SkipTextUpdate;

                int charactersToAdd = (int) (_accumulatedTime / _nextCharacterDelay);
                _accumulatedTime -= charactersToAdd * _nextCharacterDelay;

                // Lerp to our new vertical offset if it's been modified.
                int previousCount = _visibleCharactersCount;
                _visibleCharactersCount = Math.Min(_totalTextLength, _visibleCharactersCount + charactersToAdd);
                if (_visibleCharactersCount != previousCount)
                {
                    if (!_isOoc && _voicePrototype != null && _voicePrototype.Sounds.Count > 0)
                    {
                        char lastChar = _cachedRawString[_visibleCharactersCount - 1];
                        char? nextChar = _cachedRawString.Length > _visibleCharactersCount ? _cachedRawString[_visibleCharactersCount] : null;

                        var (delaySeconds, shouldPlaySound) = AnalyzeCharacter(lastChar, nextChar);
                        _nextCharacterDelay = delaySeconds;

                        bool isSpamming = ActiveBarksCount.TryGetValue(_senderEntity, out var barksCount) && barksCount > 1; // MirDi - спам защита
                        if (shouldPlaySound && !isSpamming && ConfigManager.GetCVar(Shared._WorldDi.CCVars.ChatVoiceBarksEnabled))
                        {
                            if (_entityManager.TryGetComponent<VoiceBarkComponent>(_senderEntity, out _barkComponent) && _barkComponent.VoiceId != null)
                            {
                                _prototypeManager.TryIndex(_barkComponent.VoiceId, out _voicePrototype);
                            }

                            if (_voicePrototype != null && _voicePrototype.Sounds.Count > 0)
                            {
                                var randomSound = _random.Pick(_voicePrototype.Sounds);
                                var basePitch = _barkComponent?.BasePitch ?? _voicePrototype.BasePitch;
                                var pitchVar = _barkComponent?.PitchVariation ?? _voicePrototype.PitchVariation;
                                var randomPitch = Math.Max(0.2f, basePitch + _random.NextFloat(-pitchVar, pitchVar));

                                float currentVolume = _voicePrototype.Volume;
                                if (_speechType == SpeechType.Whisper)
                                {
                                    currentVolume -= 4f;
                                }
                                var audioParams = new AudioParams { Pitch = randomPitch, Volume = currentVolume };
                                _entityManager.System<AudioSystem>().PlayLocal(randomSound, _senderEntity, null, audioParams);
                            }
                        }
                    }

                    var invisibleTailMarkup = BuildTransparencyMarkup(_rawContentMarkup, _visibleCharactersCount);
                    SpeechLabel.SetMessage(FormatSpeech(invisibleTailMarkup, _languageFontColor));
                }

                _deathTime = _timing.RealTime + TimeSpan.FromSeconds(MathF.Max(timeLeft, 2.0f));
                if (_visibleCharactersCount >= _totalTextLength && !_isBarkFinished)
                {
                    _isBarkFinished = true;

                    if (!_isOoc && _speechStyleClass != "emoteBox")
                    {
                        if (ActiveBarksCount.TryGetValue(_senderEntity, out var count))
                        {
                            if (count <= 1)
                                ActiveBarksCount.Remove(_senderEntity);
                            else
                                ActiveBarksCount[_senderEntity] = count - 1;
                        }
                    }
                }
            }

        SkipTextUpdate:

            if (MathHelper.CloseToPercent(_verticalOffsetAchieved - VerticalOffset, 0, 0.1))
            {
                _verticalOffsetAchieved = VerticalOffset;
            }
            else
            {
                _verticalOffsetAchieved = MathHelper.Lerp(_verticalOffsetAchieved, VerticalOffset, 10 * args.DeltaSeconds);
            }

            if (!_entityManager.TryGetComponent<TransformComponent>(_senderEntity, out var xform) || xform.MapID != _eyeManager.CurrentEye.Position.MapId)
            {
                Modulate = Color.White.WithAlpha(0);
                return;
            }

            if (timeLeft <= FadeTime.TotalSeconds)
            {
                // Update alpha if we're fading.
                Modulate = Color.White.WithAlpha(timeLeft / (float)FadeTime.TotalSeconds);
            }
            else
            {
                // Make opaque otherwise, because it might have been hidden before
                Modulate = Color.White;
            }

            var baseOffset = 0f;

            if (_entityManager.TryGetComponent<SpeechComponent>(_senderEntity, out var speech))
                baseOffset = speech.SpeechBubbleOffset;

            var offset = (-_eyeManager.CurrentEye.Rotation).ToWorldVec() * -(EntityVerticalOffset + baseOffset);
            var worldPos = _transformSystem.GetWorldPosition(xform) + offset;

            var lowerCenter = _eyeManager.WorldToScreen(worldPos) / UIScale;
            var screenPos = lowerCenter - new Vector2(ContentSize.X / 2, ContentSize.Y + _verticalOffsetAchieved);
            // Round to nearest 0.5
            screenPos = (screenPos * 2).Rounded() / 2;
            LayoutContainer.SetPosition(this, screenPos);

            var height = MathF.Ceiling(MathHelper.Clamp(lowerCenter.Y - screenPos.Y, 0, ContentSize.Y));
            SetHeight = height;
        }

        private (float delaySeconds, bool playSound) AnalyzeCharacter(char current, char? next)
        {
            bool isFollowedBySpaceOrEnd = !next.HasValue || next.Value == ' ';

            if (current == '.' && isFollowedBySpaceOrEnd) return (0.6f, false);
            if ((current == '!' || current == '?' || current == ';') && isFollowedBySpaceOrEnd) return (0.4f, false);
            if (current == ',' && isFollowedBySpaceOrEnd) return (0.25f, false);
            if (current == ' ') return (0.03f, false);

            return (0.03f, true);
        }

        private void Die()
        {
            if (Disposed) return;
            OnDied?.Invoke(_senderEntity, this);
        }

        /// <summary>
        ///     Causes the speech bubble to start fading IMMEDIATELY.
        /// </summary>
        public void FadeNow()
        {
            if (_deathTime > _timing.RealTime)
            {
                _deathTime = _timing.RealTime + FadeTime;
            }
        }

        protected FormattedMessage FormatSpeech(string message, Color? fontColor = null)
        {
            var msg = new FormattedMessage();
            if (fontColor != null)
                msg.PushColor(fontColor.Value);
            msg.AddMarkupOrThrow(message);
            return msg;
        }

        protected FormattedMessage ExtractAndFormatSpeechSubstring(ChatMessage message, string tag, Color? fontColor = null)
        {
            return FormatSpeech(SharedChatSystem.GetStringInsideTag(message, tag), fontColor);
        }

        private string BuildTransparencyMarkup(string markup, int visibleCharsLimit)
        {
            if (string.IsNullOrEmpty(markup)) return string.Empty;

            var result = new System.Text.StringBuilder();
            int currentVisibleCount = 0;
            bool insideTag = false;
            bool transparencyOpened = false;

            for (int i = 0; i < markup.Length; i++)
            {
                char c = markup[i];

                if (c == '[') insideTag = true;

                if (!insideTag && currentVisibleCount >= visibleCharsLimit && !transparencyOpened)
                {
                    result.Append("[color=#00000000]");
                    transparencyOpened = true;
                }

                result.Append(c);

                if (!insideTag && !transparencyOpened)
                {
                    currentVisibleCount++;
                }

                if (c == ']') insideTag = false;
            }

            if (transparencyOpened)
            {
                result.Append("[/color]");
            }

            return result.ToString();
        }
    }

    public sealed class TextSpeechBubble : SpeechBubble
    {
        public TextSpeechBubble(SpeechType type, ChatMessage message, EntityUid senderEntity, string speechStyleClass, Color? fontColor = null)
        : base(type, message, senderEntity, speechStyleClass, fontColor)
        {
        }

        protected override Control BuildBubble(ChatMessage message, string speechStyleClass, Color? fontColor = null)
        {
            var label = new RichTextLabel
            {
                MaxWidth = SpeechMaxWidth,
            };

            label.SetMessage(FormatSpeech(message.WrappedMessage, fontColor));

            SpeechLabel = label;
            var panel = new PanelContainer
            {
                StyleClasses = { "speechBox", speechStyleClass },
                Children = { label },
                ModulateSelfOverride = Color.White.WithAlpha(ConfigManager.GetCVar(CCVars.SpeechBubbleBackgroundOpacity))
            };

            return panel;
        }
    }

    public sealed class FancyTextSpeechBubble : SpeechBubble
    {

        public FancyTextSpeechBubble(SpeechType type, ChatMessage message, EntityUid senderEntity, string speechStyleClass, Color? fontColor = null)
        : base(type, message, senderEntity, speechStyleClass, fontColor)
        {
        }

        protected override Control BuildBubble(ChatMessage message, string speechStyleClass, Color? fontColor = null)
        {
            if (!ConfigManager.GetCVar(CCVars.ChatEnableFancyBubbles))
            {
                var label = new RichTextLabel
                {
                    MaxWidth = SpeechMaxWidth
                };

                label.SetMessage(ExtractAndFormatSpeechSubstring(message, "BubbleContent", fontColor), AllowedTags); // Goob - added AllowedTags
                SpeechLabel = label;

                var unfanciedPanel = new PanelContainer
                {
                    StyleClasses = { "speechBox", speechStyleClass },
                    Children = { label },
                    ModulateSelfOverride = Color.White.WithAlpha(ConfigManager.GetCVar(CCVars.SpeechBubbleBackgroundOpacity)),
                };
                return unfanciedPanel;
            }

            var bubbleHeader = new RichTextLabel
            {
                ModulateSelfOverride = Color.White.WithAlpha(ConfigManager.GetCVar(CCVars.SpeechBubbleSpeakerOpacity)),
                Margin = new Thickness(1, 1, 1, 1),
                StyleClasses = { "bubbleHeader" },
            };

            var bubbleContent = new RichTextLabel
            {
                ModulateSelfOverride = Color.White.WithAlpha(ConfigManager.GetCVar(CCVars.SpeechBubbleTextOpacity)),
                MaxWidth = SpeechMaxWidth,
                Margin = new Thickness(2, 6, 2, 2),
                StyleClasses = { "bubbleContent" },
            };

            //We'll be honest. *Yes* this is hacky. Doing this in a cleaner way would require a bottom-up refactor of how saycode handles sending chat messages. -Myr
            bubbleHeader.SetMessage(ExtractAndFormatSpeechSubstring(message, "BubbleHeader", fontColor), AllowedTags); // Goob - added AllowedTags
            bubbleContent.SetMessage(ExtractAndFormatSpeechSubstring(message, "BubbleContent", fontColor), AllowedTags); // Goob - added AllowedTags
            SpeechLabel = bubbleContent;

            //As for below: Some day this could probably be converted to xaml. But that is not today. -Myr
            var mainPanel = new PanelContainer
            {
                StyleClasses = { "speechBox", speechStyleClass },
                Children = { bubbleContent },
                ModulateSelfOverride = Color.White.WithAlpha(ConfigManager.GetCVar(CCVars.SpeechBubbleBackgroundOpacity)),
                HorizontalAlignment = HAlignment.Center,
                VerticalAlignment = VAlignment.Bottom,
                Margin = new Thickness(4, 14, 4, 2)
            };

            var headerPanel = new PanelContainer
            {
                StyleClasses = { "speechBox", speechStyleClass },
                Children = { bubbleHeader },
                ModulateSelfOverride = Color.White.WithAlpha(ConfigManager.GetCVar(CCVars.ChatFancyNameBackground) ? ConfigManager.GetCVar(CCVars.SpeechBubbleBackgroundOpacity) : 0f),
                HorizontalAlignment = HAlignment.Center,
                VerticalAlignment = VAlignment.Top
            };

            var panel = new PanelContainer
            {
                Children = { mainPanel, headerPanel }
            };

            return panel;
        }
    }
}
