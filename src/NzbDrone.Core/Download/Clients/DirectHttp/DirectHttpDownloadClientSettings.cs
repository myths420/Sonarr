using FluentValidation;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;

namespace NzbDrone.Core.Download.Clients.DirectHttp
{
    public class DirectHttpDownloadClientSettingsValidator : AbstractValidator<DirectHttpDownloadClientSettings>
    {
        public DirectHttpDownloadClientSettingsValidator()
        {
            RuleFor(c => c.DestinationDirectory).NotEmpty()
                .WithMessage("'Destination Directory' must not be empty.");
            RuleFor(c => c.DestinationDirectory).IsValidPath()
                .When(c => c.DestinationDirectory.IsNotNullOrWhiteSpace());
        }
    }

    // NOTE: this settings class intentionally has no site-specific fields
    // (no base URL, no selectors, etc.) -- resolving "what to actually
    // download" from a given episode page/embed is handled entirely by the
    // custom Indexer, which does its own scraping (port of hls.py/main.py's
    // logic) and puts a real, ready-to-fetch (or one-more-hop-away) URL into
    // ReleaseInfo.DownloadUrl. This client's only job is turning that URL
    // into bytes on disk. Per-site scraping config (the "add a website,
    // edit its scraping rules" UI you wanted) belongs on the Indexer side,
    // not here -- see the companion indexer project.
    public class DirectHttpDownloadClientSettings : DownloadClientSettingsBase<DirectHttpDownloadClientSettings>
    {
        private static readonly DirectHttpDownloadClientSettingsValidator Validator = new();

        public DirectHttpDownloadClientSettings()
        {
            MaxConcurrentDownloads = 3;
        }

        [FieldDefinition(0, Label = "Destination", Type = FieldType.Textbox, HelpText = "Folder finished (and in-progress) downloads are written to.")]
        public string DestinationDirectory { get; set; }

        [FieldDefinition(1, Label = "Category", Type = FieldType.Textbox, HelpText = "Optional subfolder under the destination directory.")]
        public string Category { get; set; }

        [FieldDefinition(2, Label = "Max Concurrent Downloads", Type = FieldType.Number, HelpText = "How many episodes to download at once. Each one is a real in-process HTTP download, not handed off to an external app.")]
        public int MaxConcurrentDownloads { get; set; }

        public override NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
