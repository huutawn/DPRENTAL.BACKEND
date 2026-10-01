namespace Dprental.__SERVICE_NAME__.Domain.IRepositories;

/// <summary>CRUD contract shared by domain-specific repositories.</summary>
/// <typeparam name="TEntity">The persisted entity type.</typeparam>
/// <typeparam name="TKey">The entity identifier type.</typeparam>
public interface IRepo<TEntity, in TKey>
    where TEntity : class
{
    /// <summary>Gets one entity by its identifier, or null when it does not exist.</summary>
    Task<TEntity?> GetByIdAsync(TKey id, CancellationToken cancellationToken = default);

    /// <summary>Gets all entities of this repository's aggregate type.</summary>
    Task<IReadOnlyList<TEntity>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists a new entity and returns it.</summary>
    Task<TEntity> CreateAsync(TEntity entity, CancellationToken cancellationToken = default);

    /// <summary>Persists the supplied entity when it already exists.</summary>
    Task<bool> UpdateAsync(TEntity entity, CancellationToken cancellationToken = default);

    /// <summary>Deletes the entity when it exists.</summary>
    Task<bool> DeleteAsync(TKey id, CancellationToken cancellationToken = default);
}
