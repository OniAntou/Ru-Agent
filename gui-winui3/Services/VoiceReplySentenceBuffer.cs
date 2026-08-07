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
        int squareDepth = 0;
        int parenthesisDepth = 0;
        int braceDepth = 0;
        int angleDepth = 0;
        int backtickDelimiterLength = 0;
        char formattingDelimiter = '\0';
        int formattingDelimiterLength = 0;

        for (int index = 0; index <= end; index++)
        {
            char current = _pending[index];
            if (current == '`')
            {
                int runLength = DelimiterRunLength(index);

                if (backtickDelimiterLength == 0)
                {
                    backtickDelimiterLength = runLength;
                }
                else if (backtickDelimiterLength == runLength)
                {
                    backtickDelimiterLength = 0;
                }

                index += runLength - 1;
                continue;
            }

            if (backtickDelimiterLength == 0)
            {
                if (current is '*' or '_' or '~')
                {
                    int runLength = DelimiterRunLength(index);
                    if (formattingDelimiterLength == 0)
                    {
                        formattingDelimiter = current;
                        formattingDelimiterLength = runLength;
                    }
                    else if (formattingDelimiter == current && formattingDelimiterLength == runLength)
                    {
                        formattingDelimiter = '\0';
                        formattingDelimiterLength = 0;
                    }

                    index += runLength - 1;
                    continue;
                }

                switch (current)
                {
                    case '[': squareDepth++; break;
                    case ']': squareDepth = Math.Max(0, squareDepth - 1); break;
                    case '(': parenthesisDepth++; break;
                    case ')': parenthesisDepth = Math.Max(0, parenthesisDepth - 1); break;
                    case '{': braceDepth++; break;
                    case '}': braceDepth = Math.Max(0, braceDepth - 1); break;
                    case '<' when HasClosingAngleDelimiter(index): angleDepth++; break;
                    case '>': angleDepth = Math.Max(0, angleDepth - 1); break;
                }
            }

            bool insideDelimitedRun = backtickDelimiterLength > 0 || squareDepth > 0 ||
                parenthesisDepth > 0 || braceDepth > 0 || angleDepth > 0 ||
                formattingDelimiterLength > 0;
            if (index >= start && char.IsWhiteSpace(current) && !insideDelimitedRun &&
                index > 0 && index + 1 < _pending.Length &&
                char.IsLetter(_pending[index - 1]) && char.IsLetter(_pending[index + 1]))
            {
                return index;
            }
        }
        return -1;
    }

    private int DelimiterRunLength(int startIndex)
    {
        char delimiter = _pending[startIndex];
        int runLength = 1;
        while (startIndex + runLength < _pending.Length && _pending[startIndex + runLength] == delimiter)
        {
            runLength++;
        }
        return runLength;
    }

    private bool HasClosingAngleDelimiter(int openingIndex)
    {
        for (int index = openingIndex + 1; index < _pending.Length; index++)
        {
            if (_pending[index] == '>')
            {
                return true;
            }
        }
        return false;
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
