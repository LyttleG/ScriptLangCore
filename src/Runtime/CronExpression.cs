namespace ScriptLangCore;

/// <summary>
/// Handcrafted CRON expression parser with O(1) field lookups and
/// performance-optimized next-occurrence scanning.
/// 
/// Supports standard cron syntax:
///   *       Any value
///   ,       Value list (e.g. 1,3,5)
///   -       Range (e.g. 1-5)
///   /       Step values (e.g. */15, 1-10/2)
///   L       Last (day of month, day of week)
///   W       Nearest weekday
///   #       Nth occurrence (e.g. 5#3 = 3rd Friday)
///   @alias  @yearly, @monthly, @weekly, @daily, @hourly
///   6th field  Optional year (e.g. "0 0 1 * * 2026")
/// 
/// Usage (from script):
///   var cron = CronExpression.Parse("*/15 8-11 * * *");
///   var next = cron.GetNextOccurrence(DateTime.Now);
/// </summary>
public sealed class CronExpression : IDisposable
{
    public void Dispose() { } // pure managed, IDisposable for uniform script using/is
    public override string ToString() => ScriptNameOf(typeof(CronExpression)) ?? "CronExpression";
    private readonly HashSet<int> _minutes = [];
    private readonly HashSet<int> _hours = [];
    private readonly HashSet<int> _daysOfMonth = [];
    private readonly HashSet<int> _months = [];
    private readonly HashSet<int> _daysOfWeek = [];
    private HashSet<int>? _years;

    // Special flags for extended syntax
    private bool _lastDayOfMonth;
    private int? _weekdayNearest;
    private int? _lastWeekday;
    private Dictionary<int, int>? _nthWeekday;

    /// <summary>
    /// Private constructor, use <see cref="Parse"/> to create instances.
    /// </summary>
    internal CronExpression() { }

    /// <summary>The original CRON expression string that was parsed.</summary>
    public string Expression { get; private set; } = string.Empty;

    /// <summary>
    /// Parses a standard 5 or 6-field CRON expression and returns a <see cref="CronExpression"/> instance.
    /// Fields: minute, hour, day-of-month, month, day-of-week [, year].
    /// Also supports @aliases: @yearly, @monthly, @weekly, @daily, @hourly.
    /// </summary>
    public static CronExpression Parse(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new ArgumentNullException(nameof(expression));

        // Handle @aliases (case-insensitive, allocation-free)
        var exprSpan = expression.AsSpan().Trim();
        if (!exprSpan.IsEmpty && exprSpan[0] == '@')
        {
            if (exprSpan.Equals("@yearly", StringComparison.OrdinalIgnoreCase)) expression = "0 0 1 1 *";
            else if (exprSpan.Equals("@monthly", StringComparison.OrdinalIgnoreCase)) expression = "0 0 1 * *";
            else if (exprSpan.Equals("@weekly", StringComparison.OrdinalIgnoreCase)) expression = "0 0 * * 0";
            else if (exprSpan.Equals("@daily", StringComparison.OrdinalIgnoreCase)) expression = "0 0 * * *";
            else if (exprSpan.Equals("@hourly", StringComparison.OrdinalIgnoreCase)) expression = "0 * * * *";
            else if (exprSpan.Equals("@reboot", StringComparison.OrdinalIgnoreCase))
                throw new FormatException("@reboot is not supported (not time-based).");
            else throw new FormatException($"Unknown CRON alias '{exprSpan}'.");
        }

        var cron = new CronExpression
        {
            Expression = expression
        };
        var expr = expression.AsSpan();

        // Split into fields (5 or 6)
        Span<Range> fieldRanges = stackalloc Range[6];
        var fieldCount = expr.Split(fieldRanges, ' ', StringSplitOptions.RemoveEmptyEntries);

        if (fieldCount < 5 || fieldCount > 6)
            throw new FormatException("Cron expression must have 5 or 6 fields (Min, Hour, DayOfMonth, Month, DayOfWeek[, Year]).");

        ParseField(expr[fieldRanges[0]], cron._minutes, 0, 59);
        ParseField(expr[fieldRanges[1]], cron._hours, 0, 23);
        ParseFieldDayMonth(expr[fieldRanges[2]], cron);
        ParseField(expr[fieldRanges[3]], cron._months, 1, 12);
        ParseFieldDayWeek(expr[fieldRanges[4]], cron);
        if (fieldCount == 6)
        {
            cron._years = [];
            ParseField(expr[fieldRanges[5]], cron._years, 1970, 2099);
        }

        return cron;
    }

    /// <summary>
    /// Calculates the next occurrence matching the cron schedule after <paramref name="baseTime"/>.
    /// The search is capped at 5 years to prevent infinite loops.
    /// </summary>
    public DateTime? GetNextOccurrence(DateTime baseTime)
    {
        // Start evaluating from the next full minute
        var testTime = baseTime.AddMinutes(1).TrimToMinute();

        // Don't loop infinitely; cap search at 5 years out
        var limit = baseTime.AddYears(5);

        while (testTime < limit)
        {
            if (_years != null && !_years.Contains(testTime.Year))
            {
                testTime = new DateTime(testTime.Year + 1, 1, 1, 0, 0, 0, testTime.Kind);
                continue;
            }

            if (!_months.Contains(testTime.Month))
            {
                testTime = new DateTime(testTime.Year, testTime.Month, 1, 0, 0, 0, testTime.Kind).AddMonths(1);
                continue;
            }

            if (!DayMatches(testTime))
            {
                testTime = new DateTime(testTime.Year, testTime.Month, testTime.Day, 0, 0, 0, testTime.Kind).AddDays(1);
                continue;
            }

            if (!_hours.Contains(testTime.Hour))
            {
                testTime = new DateTime(testTime.Year, testTime.Month, testTime.Day, testTime.Hour, 0, 0, testTime.Kind).AddHours(1);
                continue;
            }

            if (!_minutes.Contains(testTime.Minute))
            {
                testTime = testTime.AddMinutes(1);
                continue;
            }

            return testTime;
        }

        return null;
    }

    private bool DayMatches(DateTime testTime)
    {
        var day = testTime.Day;
        var dow = (int)testTime.DayOfWeek;
        var month = testTime.Month;
        var year = testTime.Year;

        // Check day-of-month: empty set = any day (unless special flags are set)
        var hasDomFlags = _lastDayOfMonth || _weekdayNearest.HasValue;
        var dayOk = (!hasDomFlags && _daysOfMonth.Count == 0) || _daysOfMonth.Contains(day);
        if (!dayOk && _lastDayOfMonth)
            dayOk = day == DateTime.DaysInMonth(year, month);
        if (!dayOk && _weekdayNearest.HasValue)
            dayOk = IsNearestWeekday(day, _weekdayNearest.Value, month, year);

        // Check day-of-week: empty set = any day (unless special flags are set)
        var hasDowFlags = _lastWeekday.HasValue || _nthWeekday != null;
        var dowOk = (!hasDowFlags && _daysOfWeek.Count == 0) || _daysOfWeek.Contains(dow);
        if (!dowOk && _lastWeekday.HasValue)
            dowOk = dow == _lastWeekday.Value && IsLastWeekdayOfMonth(day, _lastWeekday.Value, month, year);
        if (!dowOk && _nthWeekday != null && _nthWeekday.TryGetValue(dow, out var nth))
            dowOk = IsNthWeekdayOfMonth(day, dow, nth, year, month);

        // When both day-of-month and day-of-week are specified (not both *), OR logic applies
        var domSpecified = hasDomFlags || _daysOfMonth.Count > 0;
        var dowSpecified = hasDowFlags || _daysOfWeek.Count > 0;

        if (domSpecified && dowSpecified)
            return dayOk || dowOk;
        if (domSpecified)
            return dayOk;
        if (dowSpecified)
            return dowOk;
        return true; // both are *
    }

    private static bool IsNearestWeekday(int day, int target, int month, int year)
    {
        if (day != target && day != target - 1 && day != target + 1)
            return false;

        var date = new DateTime(year, month, day);
        if (date.DayOfWeek == DayOfWeek.Saturday)
            return day == target - 1 || day == target + 1; // Fri 14 or Mon 16 for 15W on Sat/Sun
        if (date.DayOfWeek == DayOfWeek.Sunday)
            return day == target + 1; // Mon 16 for 15W on Sun
        // Weekday
        if (day == target) return true;
        // Target + 1 on a Sat → Fri is target - 1 (handled above: day == target - 1)
        // Target - 1 on a Sun → Mon is target + 1 (handled above: day == target + 1)
        // If target-1 is Sat, and we're checking target+1 = Monday (already handled)
        var targetDate = new DateTime(year, month, target);
        if (targetDate.DayOfWeek == DayOfWeek.Saturday && day == target - 1) return true;
        if (targetDate.DayOfWeek == DayOfWeek.Sunday && day == target + 1) return true;
        return false;
    }

    private static bool IsLastWeekdayOfMonth(int day, int dow, int month, int year)
    {
        var lastDay = DateTime.DaysInMonth(year, month);
        // Walk backwards from the last day to find the last occurrence of this weekday
        for (var d = lastDay; d >= 1; d--)
        {
            var dt = new DateTime(year, month, d);
            if ((int)dt.DayOfWeek == dow)
                return day == d;
        }
        return false;
    }

    private static bool IsNthWeekdayOfMonth(int day, int dow, int nth, int year, int month)
    {
        var firstDay = new DateTime(year, month, 1);
        var firstDow = (int)firstDay.DayOfWeek;
        // Calculate the day of month for the Nth occurrence of the given weekday
        var offset = dow - firstDow;
        if (offset < 0) offset += 7;
        var nthDay = 1 + offset + (nth - 1) * 7;
        if (nthDay > DateTime.DaysInMonth(year, month))
            return false; // Nth occurrence doesn't exist in this month
        return day == nthDay;
    }

    private static void ParseField(ReadOnlySpan<char> token, HashSet<int> targetSet, int min, int max)
    {
        Span<Range> partRanges = stackalloc Range[16];
        var partCount = token.Split(partRanges, ',', StringSplitOptions.RemoveEmptyEntries);

        for (var pi = 0; pi < partCount; pi++)
        {
            var part = token[partRanges[pi]];

            if (part.Length == 1 && part[0] == '*')
            {
                PopulateRange(targetSet, min, max, 1);
            }
            else if (TrySplitOnce(part, '/', out var stepLeft, out var stepRight))
            {
                var step = ParseInt(stepRight);
                var rangeMin = min;
                var rangeMax = max;
                if (!(stepLeft.Length == 1 && stepLeft[0] == '*'))
                    GetRangeBounds(stepLeft, out rangeMin, out rangeMax);
                PopulateRange(targetSet, rangeMin, rangeMax, step);
            }
            else if (TrySplitOnce(part, '-', out var rangeLeft, out var rangeRight))
            {
                var rangeMin = ParseInt(rangeLeft);
                var rangeMax = ParseInt(rangeRight);
                PopulateRange(targetSet, rangeMin, rangeMax, 1);
            }
            else
            {
                var value = ParseInt(part);
                if (value < min || value > max)
                    throw new FormatException($"Value {value} out of bounds [{min}-{max}].");
                targetSet.Add(value);
            }
        }
    }

    private static void ParseFieldDayMonth(ReadOnlySpan<char> token, CronExpression cron)
    {
        Span<Range> partRanges = stackalloc Range[16];
        var partCount = token.Split(partRanges, ',', StringSplitOptions.RemoveEmptyEntries);

        for (var pi = 0; pi < partCount; pi++)
        {
            var part = token[partRanges[pi]];

            if (part.Length == 1 && part[0] == '*')
            {
                // * means any day, leave HashSet empty
            }
            else if (part.Length == 1 && part[0] == 'L')
            {
                cron._lastDayOfMonth = true;
            }
            else if (part.Length > 1 && part[^1] == 'L')
            {
                // Invalid: L has no meaning with numbers in day-of-month
                throw new FormatException($"Invalid day-of-month: '{new string(part)}'.");
            }
            else if (part.Length > 1 && part[^1] == 'W')
            {
                var numPart = part.Slice(0, part.Length - 1);
                cron._weekdayNearest = ParseInt(numPart);
            }
            else if (TrySplitOnce(part, '/', out var stepLeft, out var stepRight))
            {
                var step = ParseInt(stepRight);
                var rangeMin = 1;
                var rangeMax = 31;
                if (!(stepLeft.Length == 1 && stepLeft[0] == '*'))
                    GetRangeBounds(stepLeft, out rangeMin, out rangeMax);
                PopulateRange(cron._daysOfMonth, rangeMin, rangeMax, step);
            }
            else if (TrySplitOnce(part, '-', out var rangeLeft, out var rangeRight))
            {
                var rangeMin = ParseInt(rangeLeft);
                var rangeMax = ParseInt(rangeRight);
                PopulateRange(cron._daysOfMonth, rangeMin, rangeMax, 1);
            }
            else
            {
                var value = ParseInt(part);
                if (value < 1 || value > 31)
                    throw new FormatException($"Value {value} out of bounds [1-31].");
                cron._daysOfMonth.Add(value);
            }
        }
    }

    private static void ParseFieldDayWeek(ReadOnlySpan<char> token, CronExpression cron)
    {
        Span<Range> partRanges = stackalloc Range[16];
        var partCount = token.Split(partRanges, ',', StringSplitOptions.RemoveEmptyEntries);

        for (var pi = 0; pi < partCount; pi++)
        {
            var part = token[partRanges[pi]];

            if (part.Length == 1 && part[0] == '*')
            {
                // * means any day, leave HashSet empty
            }
            else if (part.Length == 1 && part[0] == 'L')
            {
                // L alone means last Saturday (6)
                cron._lastWeekday = 6;
            }
            else if (part.Length > 1 && part[^1] == 'L')
            {
                var numPart = part.Slice(0, part.Length - 1);
                cron._lastWeekday = ParseInt(numPart);
            }
            else if (TrySplitOnce(part, '#', out var dowPart, out var nthPart))
            {
                var dow = ParseInt(dowPart);
                var nth = ParseInt(nthPart);
                if (dow < 0 || dow > 7) throw new FormatException($"Day of week {dow} out of bounds [0-7].");
                if (dow == 7) dow = 0;
                if (nth < 1 || nth > 5) throw new FormatException($"Occurrence {nth} out of bounds [1-5].");
                cron._nthWeekday ??= new();
                cron._nthWeekday[dow] = nth;
            }
            else if (TrySplitOnce(part, '/', out var stepLeft, out var stepRight))
            {
                var step = ParseInt(stepRight);
                var rangeMin = 0;
                var rangeMax = 7;
                if (!(stepLeft.Length == 1 && stepLeft[0] == '*'))
                    GetRangeBounds(stepLeft, out rangeMin, out rangeMax);
                PopulateRange(cron._daysOfWeek, rangeMin, rangeMax, step);
                // Normalize 7 → 0: `Remove` answers whether it was there, so one lookup decides both steps.
                if (cron._daysOfWeek.Remove(7)) { cron._daysOfWeek.Add(0); }
            }
            else if (TrySplitOnce(part, '-', out var rangeLeft, out var rangeRight))
            {
                var rangeMin = ParseInt(rangeLeft);
                var rangeMax = ParseInt(rangeRight);
                PopulateRange(cron._daysOfWeek, rangeMin, rangeMax, 1);
                if (cron._daysOfWeek.Remove(7)) { cron._daysOfWeek.Add(0); }
            }
            else
            {
                var value = ParseInt(part);
                if (value < 0 || value > 7)
                    throw new FormatException($"Value {value} out of bounds [0-7].");
                if (value == 7) value = 0; // Sunday = 0
                cron._daysOfWeek.Add(value);
            }
        }
    }

    private static bool TrySplitOnce(ReadOnlySpan<char> source, char separator,
        out ReadOnlySpan<char> left, out ReadOnlySpan<char> right)
    {
        var idx = source.IndexOf(separator);
        if (idx < 0)
        {
            left = default;
            right = default;
            return false;
        }
        left = source.Slice(0, idx);
        right = source.Slice(idx + 1);
        return true;
    }

    private static void GetRangeBounds(ReadOnlySpan<char> rangeToken, out int rangeMin, out int rangeMax)
    {
        TrySplitOnce(rangeToken, '-', out var left, out var right);
        rangeMin = ParseInt(left);
        rangeMax = ParseInt(right);
    }

    private static int ParseInt(ReadOnlySpan<char> s)
    {
        try
        {
            return int.Parse(s);
        }
        catch (FormatException)
        {
            throw new FormatException($"Invalid number in CRON expression: '{new string(s)}'.");
        }
    }

    private static void PopulateRange(HashSet<int> targetSet, int start, int end, int step)
    {
        for (var i = start; i <= end; i += step)
            targetSet.Add(i);
    }
}

/// <summary>
/// Extension methods for DateTime used by CronExpression.
/// </summary>
public static class DateTimeExtensions
{
    /// <summary>
    /// Truncates the DateTime to the whole minute (zero seconds, zero milliseconds).
    /// </summary>
    public static DateTime TrimToMinute(this DateTime dt)
        => new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, 0, dt.Kind);

    /// <summary>
    /// Truncates the DateTimeOffset to the whole minute (zero seconds, zero milliseconds).
    /// </summary>
    public static DateTimeOffset TrimToMinute(this DateTimeOffset dt)
        => new DateTimeOffset(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, 0, dt.Offset);
}
