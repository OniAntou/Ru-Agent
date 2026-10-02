using System.Text;

namespace RuAgent.Cli.Tests;

/// <summary>
/// A minimal terminal screen: rows of cells with a cursor and a scrolling region,
/// implementing exactly the control set the TUI emits (CR, LF, EL, CUP, DECSTBM,
/// ED, synchronized output markers, SGR). Anything else is ignored, like a terminal
/// that does not implement it would.
/// </summary>
/// <remarks>
/// String assertions over transcript bytes cannot tell what an operator actually sees:
/// an erase sequence looks like content, and a lost row looks like nothing. Every
/// rendering assertion that matters belongs here, against the final visible screen.
/// </remarks>
internal sealed class TuiScreenSimulator
{
    private readonly int _rows;
    private readonly int _cols;
    private readonly char[][] _cells;
    private int _row;
    private int _col;
    private int _regionTop;
    private int _regionBottom;

    public TuiScreenSimulator(int rows = 41, int cols = 120)
    {
        _rows = rows;
        _cols = cols;
        _cells = new char[rows][];
        for (var i = 0; i < rows; i++)
        {
            _cells[i] = new string(' ', cols).ToCharArray();
        }

        _regionTop = 0;
        _regionBottom = rows - 1;
    }

    public void Write(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\r')
            {
                _col = 0;
                i++;
            }
            else if (c == '\n')
            {
                NewLine();
                i++;
            }
            else if (c == '\u001b')
            {
                i = ConsumeEscape(text, i);
            }
            else if (c == '\t')
            {
                // Tab stops every 8 cells, mirroring TuiSurface's tracked cursor exactly:
                // the simulator and the tracker must agree, or the test proves nothing.
                var target = (_col / 8 + 1) * 8;
                while (_col < target)
                {
                    Put(' ');
                }

                i++;
            }
            else if (char.IsControl(c))
            {
                i++;
            }
            else
            {
                Put(c);
                i++;
            }
        }
    }

    /// <summary>The whole screen, trailing blanks trimmed per row, empty rows dropped.</summary>
    public IReadOnlyList<string> VisibleRows()
    {
        var rows = new List<string>();
        foreach (var line in _cells)
        {
            var row = new string(line).TrimEnd();
            if (row.Length > 0)
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    public string ScreenText() => string.Join("\n", VisibleRows());

    /// <summary>
    /// One row's visible text with trailing blanks trimmed, addressed by absolute row so a
    /// test can assert region membership (header, transcript, footer) instead of order.
    /// Out-of-range rows are empty.
    /// </summary>
    public string VisibleRow(int row)
    {
        if (row < 0 || row >= _rows)
        {
            return string.Empty;
        }

        return new string(_cells[row]).TrimEnd();
    }

    private void Put(char c)
    {
        if (_col >= _cols)
        {
            _col = 0;
            NewLine();
        }

        _cells[_row][_col] = c;
        _col++;
    }

    private void NewLine()
    {
        if (_row == _regionBottom)
        {
            for (var r = _regionTop; r < _regionBottom; r++)
            {
                Array.Copy(_cells[r + 1], _cells[r], _cols);
            }

            Array.Fill(_cells[_regionBottom], ' ');
        }
        else if (_row < _rows - 1)
        {
            _row++;
        }
        else
        {
            for (var r = 0; r < _rows - 1; r++)
            {
                Array.Copy(_cells[r + 1], _cells[r], _cols);
            }

            Array.Fill(_cells[_rows - 1], ' ');
        }

        _col = 0;
    }

    private int ConsumeEscape(string text, int at)
    {
        // Only CSI is emitted by the TUI. Anything else is skipped safely.
        if (at + 1 >= text.Length || text[at + 1] != '[')
        {
            return at + 1;
        }

        var i = at + 2;
        var isPrivate = false;
        if (i < text.Length && text[i] == '?')
        {
            isPrivate = true;
            i++;
        }

        var parameters = new StringBuilder();
        while (i < text.Length && (char.IsDigit(text[i]) || text[i] == ';'))
        {
            parameters.Append(text[i]);
            i++;
        }

        if (i >= text.Length)
        {
            return text.Length;
        }

        var final = text[i];
        i++;
        if (isPrivate)
        {
            // Synchronized-output markers and friends: no visible effect here.
            return i;
        }

        var parts = parameters.ToString().Split(';', StringSplitOptions.RemoveEmptyEntries);
        var p1 = parts.Length > 0 && int.TryParse(parts[0], out var a) ? a : 0;
        var p2 = parts.Length > 1 && int.TryParse(parts[1], out var b) ? b : 0;

        switch (final)
        {
            case 'H':
            case 'f':
                _row = Math.Clamp((p1 == 0 ? 1 : p1) - 1, 0, _rows - 1);
                _col = Math.Clamp((p2 == 0 ? 1 : p2) - 1, 0, _cols - 1);
                break;
            case 'A':
                _row = Math.Max(0, _row - Math.Max(1, p1));
                break;
            case 'B':
                _row = Math.Min(_rows - 1, _row + Math.Max(1, p1));
                break;
            case 'C':
                _col = Math.Min(_cols - 1, _col + Math.Max(1, p1));
                break;
            case 'D':
                _col = Math.Max(0, _col - Math.Max(1, p1));
                break;
            case 'G':
                _col = Math.Clamp((p1 == 0 ? 1 : p1) - 1, 0, _cols - 1);
                break;
            case 'J':
                if (p1 == 2 || p1 == 3)
                {
                    foreach (var line in _cells)
                    {
                        Array.Fill(line, ' ');
                    }

                    _row = 0;
                    _col = 0;
                }
                else if (p1 == 1)
                {
                    for (var r = 0; r < _row; r++)
                    {
                        Array.Fill(_cells[r], ' ');
                    }

                    for (var c = 0; c <= _col && c < _cols; c++)
                    {
                        _cells[_row][c] = ' ';
                    }
                }
                else
                {
                    for (var c = _col; c < _cols; c++)
                    {
                        _cells[_row][c] = ' ';
                    }

                    for (var r = _row + 1; r < _rows; r++)
                    {
                        Array.Fill(_cells[r], ' ');
                    }
                }

                break;
            case 'K':
                if (p1 == 2)
                {
                    Array.Fill(_cells[_row], ' ');
                }
                else if (p1 == 1)
                {
                    for (var c = 0; c <= _col && c < _cols; c++)
                    {
                        _cells[_row][c] = ' ';
                    }
                }
                else
                {
                    for (var c = _col; c < _cols; c++)
                    {
                        _cells[_row][c] = ' ';
                    }
                }

                break;
            case 'r':
                if (parts.Length >= 2 && p1 >= 1 && p2 >= p1)
                {
                    _regionTop = Math.Clamp(p1 - 1, 0, _rows - 1);
                    _regionBottom = Math.Clamp(p2 - 1, 0, _rows - 1);
                }
                else
                {
                    _regionTop = 0;
                    _regionBottom = _rows - 1;
                }

                break;
            case 'm':
                break;
            default:
                break;
        }

        return i;
    }
}
