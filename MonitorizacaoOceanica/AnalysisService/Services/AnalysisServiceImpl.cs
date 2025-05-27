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
            var result = new AnalysisResult();

            // 1) Extrai todos os valores
            var vals = request.Samples.Select(s => s.Valor).ToList();
            if (vals.Count == 0)
            {
                // Sem amostras, devolve zeros
                return Task.FromResult(result);
            }

            // 2) Calcula média e desvio-padrão
            double media = vals.Average();
            double dp = Math.Sqrt(vals.Average(v => Math.Pow(v - media, 2)));

            result.Media = media;
            result.Desviopadrao = dp;

            // 3) Detecta outliers (> 2·σ)
            double limiar = 2 * dp;
            foreach (var sample in request.Samples)
            {
                if (Math.Abs(sample.Valor - media) > limiar)
                {
                    // usa "Origem:Timestamp" como chave
                    string chave = $"{sample.Origem}:{sample.Timestamp}";
                    result.Outliers[chave] = sample.Valor;
                }
            }

            return Task.FromResult(result);
        }
    }
}