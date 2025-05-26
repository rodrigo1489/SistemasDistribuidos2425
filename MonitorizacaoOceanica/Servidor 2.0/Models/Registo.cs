using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Servidor20.Models
{
    public class Registo
    {
        public int Id { get; set; }
        public string TipoMensagem { get; set; } = null!;
        public string AgregadorId { get; set; } = default!;
        public string? WavyId { get; set; }
        public string? TipoDado { get; set; } = default!;
        public double? Valor { get; set; }
        public int? Volume { get; set; }
        public string? Metodo { get; set; }
        public DateTime Timestamp { get; set; }
        public string Origem { get; set; } = default!;
        public string Destino { get; set; } = default!;
        public double? Media { get; set; }
        public double? DesvioPadrao { get; set; }
    }
}
