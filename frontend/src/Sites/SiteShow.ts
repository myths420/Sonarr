import ModelBase from 'App/ModelBase';

// Mirrors Sonarr.Api.V5.AnimeSite.SiteShowResource.
export interface SiteShow extends ModelBase {
  sourceListId: number;
  slug: string;
  title: string;
  url: string;
  posterUrl?: string;
  overview?: string;
  year: number;
  episodes: number;
  status?: string;
  genres: string[];
  aniListId: number;

  // Set when a Sonarr series with a matching title is already in the library.
  seriesId?: number;
  seriesTitleSlug?: string;
}

export default SiteShow;
