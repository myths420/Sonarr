using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.AnimeSite;
using NzbDrone.Core.Localization;
using NzbDrone.Core.ThingiProvider.Events;

namespace NzbDrone.Core.HealthCheck.Checks
{
    // Warns when an AnimeSite indexer's manually-pasted browser session
    // (cf_clearance + User-Agent) has started coming back 403 -- i.e. the
    // imported session has expired and needs re-pasting from the browser.
    // Cleared automatically once a request with that session succeeds again.
    [CheckOn(typeof(ProviderUpdatedEvent<IIndexer>))]
    [CheckOn(typeof(ProviderDeletedEvent<IIndexer>))]
    public class AnimeSiteSessionCheck : HealthCheckBase
    {
        public AnimeSiteSessionCheck(ILocalizationService localizationService)
            : base(localizationService)
        {
        }

        public override bool CheckOnStartup => false;

        public override HealthCheck Check()
        {
            var expired = AnimeSiteSessionStatus.ExpiredDomains;

            if (expired.Count == 0)
            {
                return new HealthCheck(GetType());
            }

            return new HealthCheck(GetType(),
                HealthCheckResult.Warning,
                HealthCheckReason.AnimeSiteSessionExpired,
                _localizationService.GetLocalizedString("AnimeSiteSessionExpiredHealthCheckMessage", new Dictionary<string, object>
                {
                    { "domains", string.Join(", ", expired.ToList()) }
                }));
        }
    }
}
