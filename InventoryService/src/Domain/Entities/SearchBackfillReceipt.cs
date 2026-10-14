using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Dprental.InventoryService.Domain.Entities;

[Table("search_backfill_receipts", Schema = "inventory")]
public sealed class SearchBackfillReceipt
{
    [Key, Column("id")]
    public Guid Id { get; set; }
    [Column("run_id")]
    public Guid RunId { get; set; }
    [Column("message_id")]
    public Guid MessageId { get; set; }
}
