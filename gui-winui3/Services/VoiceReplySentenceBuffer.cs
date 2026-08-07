using System;
using System.Collections.Generic;
using System.Text;

namespace RuApp.Services;

public sealed class VoiceReplySentenceBuffer
{
    private const int FirstPhraseMinimumChars = 48;
    private const int FirstPhrasePreferredChars = 60;
    private const int FirstPhraseMaximumChars = 72;

    private readonly StringBuilder _pending = new();
    private bool _hasEmittedFirstSpeechUnit;

    public IReadOnlyList<string> Append(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            _pending.Append(text);
        }
        return Drain(flush: false);
    }

    public IReadOnlyList<string> Flush() => Drain(flush: true);

    private IReadOnlyList<string> Drain(bool flush)
    {
        var sentences = new List<string>();
        while (_pending.Length > 0)
        {
            int boundary = FindBoundary();
            if (boundary < 0)
            {
                if (!_hasEmittedFirstSpeechUnit)
                {
                    int phraseBoundary = FindFirstPhraseBoundary();
                    if (phraseBoundary >= 0)
                    {
                        AddSentence(sentences, phraseBoundary);
                    }
                }
                break;
            }

            AddSentence(sentences, boundary + 1);
        }

        if (flush && _pending.Length > 0)
        {
            AddSentence(sentences, _pending.Length);
        }
        return sentences;
    }

    private int FindBoundary()
    {
        for (int index = 0; index < _pending.Length; index++)
        {
            char current = _pending[index];
            if (current is not ('.' or '!' or '?' or '。' or '！' or '？' or '…'))
            {
                continue;
            }

            if (current == '.' && index > 0 && index + 1 < _pending.Length &&
                char.IsDigit(_pending[index - 1]) && char.IsDigit(_pending[index + 1]))
            {
                continue;
            }

            if (index + 1 == _pending.Length || char.IsWhiteSpace(_pending[index + 1]))
            {
                return index;
            }
        }
        return -1;
    }

    private int FindFirstPhraseBoundary()
    {
        int preferredBoundary = FindPhraseBoundary(FirstPhraseMinimumChars, FirstPhrasePreferredChars);
        return preferredBoundary >= 0
            ? preferredBoundary
            : FindPhraseBoundary(FirstPhrasePreferredChars + 1, FirstPhraseMaximumChars);
    }

    private int FindPhraseBoundary(int minimumChars, int maximumChars)
    {
        int start = Math.Max(0, minimumChars);
        int end = Math.Min(_pending.Length - 1, maximumChars);
        for (int index = start; index <= end; index++)
        {
            if (char.IsWhiteSpace(_pending[index]))
            {
                return index;
            }
        }
        return -1;
    }

    private void AddSentence(List<string> sentences, int length)
    {
        string sentence = _pending.ToString(0, length).Trim();
        _pending.Remove(0, length);
        if (!string.IsNullOrWhiteSpace(sentence))
        {
            sentences.Add(sentence);
            _hasEmittedFirstSpeechUnit = true;
        }
    }
}
