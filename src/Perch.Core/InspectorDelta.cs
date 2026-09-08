using System;
using System.Collections.Generic;

namespace Perch;

internal static class InspectorDelta
{
    public static int CommonPrefix(InspectorData? previous, InspectorData? next)
    {
        if (next == null || previous == null) return 0;
        if (ReferenceEquals(previous, next)) return next.Events.Count;
        int i = 0;
        while (i < previous.Events.Count && i < next.Events.Count && previous.Events[i] == next.Events[i]) i++;
        return i;
    }
}
