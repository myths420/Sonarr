using System.Collections.Generic;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Common
{
    public class PathEqualityComparer : IEqualityComparer<string>
    {
        public static readonly PathEqualityComparer Instance = new PathEqualityComparer();

        private PathEqualityComparer()
        {
        }

        public bool Equals(string x, string y)
        {
            return x.PathEquals(y);
        }

        public int GetHashCode(string obj)
        {
            // Case-insensitive -- see DiskProviderBase.PathStringComparison.
            return obj.CleanFilePath().Normalize().ToLowerInvariant().GetHashCode();
        }
    }
}
