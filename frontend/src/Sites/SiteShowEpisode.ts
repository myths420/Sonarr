export interface SiteShowEpisode {
  number: number;
  title: string;
  url: string;

  // True when the linked library series already has a file for this episode.
  hasFile?: boolean;
}

export default SiteShowEpisode;
