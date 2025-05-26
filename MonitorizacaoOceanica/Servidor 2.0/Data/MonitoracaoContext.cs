using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Servidor20.Models;
using Microsoft.Extensions.Configuration;

namespace Servidor20.Data
{
    public class MonitoracaoContext : DbContext
    {
        // 1) Construtor que o AddDbContext vai usar:
        public MonitoracaoContext(DbContextOptions<MonitoracaoContext> options)
            : base(options)
        {
        }

        // 2) Construtor parameterless, para chamadas diretas com "new"
        public MonitoracaoContext()
        {
        }

        // Fallback para configuração de connection string
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured)
            {
                optionsBuilder.UseSqlServer(
                  "Server=localhost,1433;" +
                  "Database=MonitorizacaoOceanica;" +
                  "Trusted_Connection=True;" +
                  "Encrypt=False;" +
                  "TrustServerCertificate=True;");
            }
        }

        public DbSet<Registo> Registos { get; set; } = null!;
    }
}