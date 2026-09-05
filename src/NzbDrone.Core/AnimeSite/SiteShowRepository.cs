using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.AnimeSite
{
    public interface ISiteShowRepository : IBasicRepository<SiteShow>
    {
        List<SiteShow> FindBySourceList(int sourceListId);
        SiteShow FindBySlug(int sourceListId, string slug);
    }

    public class SiteShowRepository : BasicRepository<SiteShow>, ISiteShowRepository
    {
        public SiteShowRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        public List<SiteShow> FindBySourceList(int sourceListId)
        {
            return Query(s => s.SourceListId == sourceListId);
        }

        public SiteShow FindBySlug(int sourceListId, string slug)
        {
            return Query(s => s.SourceListId == sourceListId && s.Slug == slug).SingleOrDefault();
        }
    }
}
