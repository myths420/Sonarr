import ModelBase from 'App/ModelBase';

// A configured AnimeSite indexer, as shown in the Sites nav.
export interface Site extends ModelBase {
  name: string;
  implementation: string;
}

export default Site;
