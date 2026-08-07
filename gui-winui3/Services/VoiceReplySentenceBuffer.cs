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
        var protection = new ProtectedSpanState();
        for (int index = 0; index < _pending.Length; index++)
        {
            char current = _pending[index];
            int delimiterLength = protection.Consume(this, index, current);
            if (current is not ('.' or '!' or '?' or '。' or '！' or '？' or '…'))
            {
                index += delimiterLength - 1;
                continue;
            }

            if (protection.IsProtected)
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
        var protection = new ProtectedSpanState();

        for (int index = 0; index <= end; index++)
        {
            char current = _pending[index];
            int delimiterLength = protection.Consume(this, index, current);
            if (index >= start && char.IsWhiteSpace(current) && !protection.IsProtected &&
                index > 0 && index + 1 < _pending.Length &&
                char.IsLetter(_pending[index - 1]) && char.IsLetter(_pending[index + 1]))
            {
                return index;
            }
            index += delimiterLength - 1;
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

    private sealed class ProtectedSpanState
    {
        private int _squareDepth;
        private int _parenthesisDepth;
        private int _braceDepth;
        private int _angleDepth;
        private int _backtickDelimiterLength;
        private char _formattingDelimiter;
        private int _formattingDelimiterLength;

        public bool IsProtected => _backtickDelimiterLength > 0 || _squareDepth > 0 ||
            _parenthesisDepth > 0 || _braceDepth > 0 || _angleDepth > 0 ||
            _formattingDelimiterLength > 0;

        public int Consume(VoiceReplySentenceBuffer owner, int index, char current)
        {
            if (current == '`')
            {
                int runLength = owner.DelimiterRunLength(index);
                if (_backtickDelimiterLength == 0)
                {
                    _backtickDelimiterLength = runLength;
                }
                else if (_backtickDelimiterLength == runLength)
                {
                    _backtickDelimiterLength = 0;
                }
                return runLength;
            }

            bool insideStructuredRun = _squareDepth > 0 || _parenthesisDepth > 0 ||
                _braceDepth > 0 || _angleDepth > 0;
            if (_backtickDelimiterLength == 0 && !insideStructuredRun &&
                current is '*' or '_' or '~')
            {
                int runLength = owner.DelimiterRunLength(index);
                if (_formattingDelimiterLength == 0)
                {
                    _formattingDelimiter = current;
                    _formattingDelimiterLength = runLength;
                }
                else if (_formattingDelimiter == current && _formattingDelimiterLength == runLength)
                {
                    _formattingDelimiter = '\0';
                    _formattingDelimiterLength = 0;
                }
                return runLength;
            }

            if (_backtickDelimiterLength == 0)
            {
                switch (current)
                {
                    case '[': _squareDepth++; break;
                    case ']': _squareDepth = Math.Max(0, _squareDepth - 1); break;
                    case '(': _parenthesisDepth++; break;
                    case ')': _parenthesisDepth = Math.Max(0, _parenthesisDepth - 1); break;
                    case '{': _braceDepth++; break;
                    case '}': _braceDepth = Math.Max(0, _braceDepth - 1); break;
                    case '<' when owner.HasClosingAngleDelimiter(index): _angleDepth++; break;
                    case '>': _angleDepth = Math.Max(0, _angleDepth - 1); break;
                }
            }
            return 1;
        }
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
