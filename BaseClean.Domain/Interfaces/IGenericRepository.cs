using BaseClean.Domain.Entities;

namespace BaseClean.Domain.Interfaces;

public interface IGenericRepository<TEntity,TKey> where TEntity : BaseEntity<TKey>
{
    Task<TEntity?> GetByIdAsync(TKey id, CancellationToken cancellationToken = default);
    Task<List<TEntity>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<TKey> AddAsync(TEntity entity, CancellationToken cancellationToken = default);
    Task Update(TEntity entity, CancellationToken cancellationToken = default);
    Task Delete(TEntity entity, CancellationToken cancellationToken = default);
    IQueryable<TEntity> GetAll();
}