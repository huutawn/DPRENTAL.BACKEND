using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Entities;

[Table("rental_terms_acceptances", Schema = "rental")]
[Index(nameof(RentalOrderId), IsUnique = true)]
public class RentalTermsAcceptance
{
    [Key]
    public Guid Id { get; private set; }

    public Guid RentalOrderId { get; private set; }

    public Guid RenterId { get; private set; }

    [Required]
    [MaxLength(64)]
    public string TermsVersion { get; private set; } = string.Empty;

    [Required]
    [MaxLength(128)]
    public string TermsContentHash { get; private set; } = string.Empty;

    public DateTimeOffset AcceptedAt { get; private set; }

    [ForeignKey(nameof(RentalOrderId))]
    public RentalOrder? RentalOrder { get; private set; }

    private RentalTermsAcceptance()
    {
    }

    internal RentalTermsAcceptance(
        Guid id,
        Guid rentalOrderId,
        Guid renterId,
        string termsVersion,
        string termsContentHash,
        DateTimeOffset acceptedAt)
    {
        Id = id;
        RentalOrderId = rentalOrderId;
        RenterId = renterId;
        TermsVersion = termsVersion;
        TermsContentHash = termsContentHash;
        AcceptedAt = acceptedAt;
    }
}
