import ModelBase from 'App/ModelBase';

// A configured AnimeSite indexer, as far as the Sites catalogue feature
// cares -- just enough to list sites in the nav and know which indexer id
// to sync/browse. One indexer = one site.
export interface Site extends ModelBase {
  name: string;
  implementation: string;
}

export default Site;
