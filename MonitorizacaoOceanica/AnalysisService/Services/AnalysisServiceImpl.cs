using System;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Core;
using Analyze;  // ← namespace do stub gerado de analyze.proto

namespace AnalysisService.Services
{
    // Herdamos de Analyze.AnalysisService.AnalysisServiceBase
    public class AnalysisServiceImpl
        : Analyze.AnalysisService.AnalysisServiceBase
    {
        public override Task<AnalysisResult> Analyze(AnalyzeRequest request, ServerCallContext context)
        {
            // Extrai valores para calcular estatísticas
            var vals = request.Samples.Select(s => s.Valor).ToList();
            double media = vals.Average();
            double dp = Math.Sqrt(vals.Average(v => Math.Pow(v - media, 2)));

            var result = new AnalysisResult
            {
                Media = media,
                Desviopadrao = dp
            };
            // Aqui podes acrescentar detecção de outliers, HPC, etc.

            return Task.FromResult(result);
        }
    }
}
