import ModelBase from 'App/ModelBase';

// A configured AnimeSite import list instance, as far as the Sites
// catalogue feature cares -- just enough to list sites in the picker and
// know which import list id to sync/browse.
export interface Site extends ModelBase {
  name: string;
  implementation: string;
}

export default Site;
