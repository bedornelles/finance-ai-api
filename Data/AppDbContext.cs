using Microsoft.EntityFrameworkCore;
using RegistrAi.Api.Models;

namespace RegistrAi.Api.Data{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base (options)
        {

        }
        //isto vira a tabela no postrgres
        public DbSet<Transacao> Transacoes {get; set;} = null!;
    }
}