using Microsoft.AspNetCore.Mvc;
using RegistrAi.Api.Data;
using RegistrAi.Api.Models;
using System.Text.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace RegistrAi.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class TransacoesIaController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly string _apiKey;
        private readonly HttpClient _httpClient;

        public TransacoesIaController(AppDbContext context, IConfiguration configuration, IHttpClientFactory httpClientFactory)
        {
            _context = context;
            _apiKey = configuration["Gemini:ApiKey"]!; 
            _httpClient = httpClientFactory.CreateClient();
        }

        public class RequisicaoChat
        {
            public string Texto { get; set; } = string.Empty;

             public List<MensagemHistorico> Historico { get; set; } = new();
            // Histórico da conversa — Flutter guarda e manda a cada mensagem
            // Ex: [{ role: "user", content: "gastei 50" }, { role: "assistant", content: "Qual a categoria?" }]

            public int Tentativas { get; set; } = 0;
            // Conta quantas vezes a IA já pediu informação faltando
            // Flutter incrementa a cada pergunta da IA
            // Quando chega em 2 → API retorna mensagem de erro
        }

        public class MensagemHistorico
        {
            public string Role { get; set; } = string.Empty;
            // "user" = mensagem do usuário
            // "assistant" = mensagem da IA

            public string Content { get; set; } = string.Empty;
            // O texto da mensagem
        }

        // O que a API devolve para o Flutter
        public class RespostaChat
        {
            public string Tipo { get; set; } = string.Empty;
            // "confirmacao_pendente" → IA entendeu tudo, aguarda sim/não
            // "pergunta_info"        → IA precisa de mais informação
            // "pergunta"             → usuário fez uma pergunta, IA respondeu
            // "erro"                 → IA não conseguiu entender após 2 tentativas

            public string Mensagem { get; set; } = string.Empty;
            // O texto que aparece no chat para o usuário

            public Transacao? TransacaoPendente { get; set; }
            // Preenchido só quando tipo == "confirmacao_pendente"
            // Flutter guarda isso para mandar ao /confirmar quando usuário digitar "sim"
        }

       private string MontarPrompt(string textoUsuario, List<MensagemHistorico> historico)
        {
            string dataAtual = DateTime.Now.ToString("yyyy-MM-dd");

            // Monta o histórico da conversa em texto
            // para a IA entender o contexto do que já foi dito
            var historicoTexto = string.Join("\n", historico
                .Select(h => $"{(h.Role == "user" ? "Usuário" : "Assistente")}: {h.Content}"));

            return $@"
                Você é um assistente financeiro pessoal chamado RegistrAI.
                A data de HOJE é {dataAtual}.

                Você recebe mensagens de texto e deve classificá-las em 3 tipos:

                ════════════════════════════════════
                TIPO 1 — REGISTRO DE TRANSAÇÃO
                ════════════════════════════════════
                O usuário quer registrar uma despesa ou receita.
                Exemplos: 'gastei 50 no almoço', 'recebi meu salário de 3000'

                Verifique se tem TODOS esses campos:
                - valor (número)
                - categoria (assunto do gasto/receita)
                - tipo ('Despesa' ou 'Receita')
                - data (se não mencionar, use a data de hoje: {dataAtual})

                Se tiver TUDO → retorne APENAS este JSON:
                {{
                    ""classificacao"": ""confirmacao_pendente"",
                    ""mensagem"": ""Vou registrar R$[valor] em [categoria] em [data formatada dd/MM/yyyy]. Confirma?"",
                    ""transacao"": {{
                        ""valor"": (número decimal com ponto),
                        ""categoria"": (texto curto),
                        ""data"": (formato ISO: YYYY-MM-DDTHH:mm:ss),
                        ""tipo"": (apenas 'Despesa' ou 'Receita')
                    }}
                }}

                Se faltar algum campo → retorne APENAS este JSON:
                {{
                    ""classificacao"": ""pergunta_info"",
                    ""mensagem"": ""[pergunta objetiva sobre o dado que falta]"",
                    ""transacao"": null
                }}

                ════════════════════════════════════
                TIPO 2 — PERGUNTA SOBRE FINANÇAS
                ════════════════════════════════════
                O usuário quer saber informações sobre seus gastos.
                Exemplos: 'quanto gastei essa semana?', 'qual meu saldo de maio?'

                Retorne APENAS este JSON:
                {{
                    ""classificacao"": ""pergunta"",
                    ""filtros"": {{
                        ""periodo"": (""hoje"", ""semana"", ""mes"", ""ano"" ou null),
                        ""categoria"": (categoria mencionada ou null),
                        ""tipo"": (""Despesa"", ""Receita"" ou null),
                        ""dataInicio"": (data específica ou null),
                        ""dataFim"": (data específica ou null)
                    }},
                    ""mensagem"": null,
                    ""transacao"": null
                }}

                ════════════════════════════════════
                TIPO 3 — CONFIRMAÇÃO
                ════════════════════════════════════
                O usuário respondeu 'sim', 'confirmo', 'pode salvar' ou similar.
                Ou respondeu 'não', 'cancela', 'para' ou similar.

                Se confirmou → retorne APENAS este JSON:
                {{
                    ""classificacao"": ""confirmado"",
                    ""mensagem"": null,
                    ""transacao"": null
                }}

                Se cancelou → retorne APENAS este JSON:
                {{
                    ""classificacao"": ""cancelado"",
                    ""mensagem"": ""Ok, a transação não foi registrada."",
                    ""transacao"": null
                }}

                ════════════════════════════════════
                REGRAS IMPORTANTES
                ════════════════════════════════════
                - Retorne SEMPRE apenas JSON válido, sem markdown, sem texto fora do JSON
                - Se o usuário falar 'ontem', subtraia 1 dia de {dataAtual}
                - Seja objetivo nas perguntas: 'Qual o valor?' e não 'Poderia me informar o valor?'
                - A categoria deve ser curta: 'Alimentação', 'Transporte', 'Saúde', 'Mercado', etc

                ════════════════════════════════════
                HISTÓRICO DA CONVERSA
                ════════════════════════════════════
                {(string.IsNullOrEmpty(historicoTexto) ? "Nenhum histórico ainda." : historicoTexto)}

                Mensagem atual do usuário: '{textoUsuario}'";
            }

        [HttpPost("interpretar")]
        public async Task<IActionResult> Interpretar([FromBody] RequisicaoChat requisicao)
        {
            // VERIFICAÇÃO DO LIMITE DE TENTATIVAS
            // Se o Flutter já mandou 2 tentativas sem sucesso,
            // nem chama a IA — retorna erro direto
            if (requisicao.Tentativas >= 2)
            {
                return Ok(new RespostaChat
                {
                    Tipo = "erro",
                    Mensagem = "Não consegui entender. Tente enviar assim: 'Gastei R$50 em alimentação hoje'",
                    TransacaoPendente = null
                });
            }

            // MONTA E ENVIA O PROMPT PARA O GEMINI
            string prompt = MontarPrompt(requisicao.Texto, requisicao.Historico);

            var corpoRequisicao = new
            {
                contents = new[]
                {
                    new { parts = new[] { new { text = prompt } } }
                }
            };

            string jsonEnvio = JsonSerializer.Serialize(corpoRequisicao);
            var conteudo = new StringContent(jsonEnvio, Encoding.UTF8, "application/json");
            string urlGoogle = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={_apiKey}";

            var respostaGoogle = await _httpClient.PostAsync(urlGoogle, conteudo);

            if (!respostaGoogle.IsSuccessStatusCode)
            {
                string erroDetalhado = await respostaGoogle.Content.ReadAsStringAsync();
                return StatusCode(500, $"Erro ao chamar a IA: {erroDetalhado}");
            }

            // LÊ E LIMPA A RESPOSTA DO GEMINI
            string jsonResposta = await respostaGoogle.Content.ReadAsStringAsync();

            using var documento = JsonDocument.Parse(jsonResposta);
            var textoIa = documento.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text").GetString();

            textoIa = textoIa!.Replace("```json", "").Replace("```", "").Trim();

            Console.WriteLine($"RESPOSTA DA IA: {textoIa}");

            // CONVERTE O JSON DA IA PARA UM OBJETO C#
            using var respostaIa = JsonDocument.Parse(textoIa);
            var classificacao = respostaIa.RootElement
                .GetProperty("classificacao").GetString();

            // ROTEAMENTO — decide o que fazer baseado na classificação da IA
            switch (classificacao)
            {
                // USUÁRIO QUER REGISTRAR E TEM TUDO NECESSÁRIO
                case "confirmacao_pendente":
                    var transacaoJson = respostaIa.RootElement
                        .GetProperty("transacao").ToString();

                    var transacaoPendente = JsonSerializer.Deserialize<Transacao>(
                        transacaoJson,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                    );

                    // Normaliza o Tipo (mesma lógica que já tínhamos)
                    var tipoNormalizado = transacaoPendente!.Tipo?.Trim().ToLower();
                    transacaoPendente.Tipo = char.ToUpper(tipoNormalizado![0]) + tipoNormalizado[1..];

                    var mensagemConfirmacao = respostaIa.RootElement
                        .GetProperty("mensagem").GetString();

                    return Ok(new RespostaChat
                    {
                        Tipo = "confirmacao_pendente",
                        Mensagem = mensagemConfirmacao!,
                        TransacaoPendente = transacaoPendente
                    });

                // IA PRECISA DE MAIS INFORMAÇÃO
                case "pergunta_info":
                    var pergunta = respostaIa.RootElement
                        .GetProperty("mensagem").GetString();

                    return Ok(new RespostaChat
                    {
                        Tipo = "pergunta_info",
                        Mensagem = pergunta!,
                        TransacaoPendente = null
                    });

                // USUÁRIO FEZ UMA PERGUNTA SOBRE SEUS GASTOS
                case "pergunta":
                {
                    var filtrosElement = respostaIa.RootElement.GetProperty("filtros");

                    // Extrai os filtros que a IA identificou na pergunta
                    var periodo = filtrosElement.TryGetProperty("periodo", out var p) ? p.GetString() : null;
                    var categoria = filtrosElement.TryGetProperty("categoria", out var c) ? c.GetString() : null;
                    var tipo = filtrosElement.TryGetProperty("tipo", out var tp) ? tp.GetString() : null;

                    // Calcula as datas baseado no período
                    var hoje = DateTime.Today;
                    DateTime dataInicio;
                    DateTime dataFim = hoje;

                    switch (periodo)
                    {
                        case "hoje":
                            dataInicio = hoje;
                            break;
                        case "semana":
                            var diasDesdeSegunda = ((int)hoje.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
                            dataInicio = hoje.AddDays(-diasDesdeSegunda);
                            break;
                        case "ano":
                            dataInicio = new DateTime(hoje.Year, 1, 1);
                            break;
                        default: // mes
                            dataInicio = new DateTime(hoje.Year, hoje.Month, 1);
                            break;
                    }

                    // Consulta o banco com os filtros
                    var query = _context.Transacoes.AsQueryable();
                    query = query.Where(t => t.Data >= dataInicio && t.Data <= dataFim);

                    if (!string.IsNullOrEmpty(categoria))
                        query = query.Where(t => t.Categoria.ToLower().Contains(categoria.ToLower()));

                    if (!string.IsNullOrEmpty(tipo))
                        query = query.Where(t => t.Tipo == tipo);

                    var transacoesFiltradas = await query.ToListAsync();

                    // Monta o resumo para a IA formatar a resposta
                    var totalConsulta = transacoesFiltradas.Sum(t => t.Valor);
                    var quantidadeConsulta = transacoesFiltradas.Count;
                    var porCategoriaConsulta = transacoesFiltradas
                        .GroupBy(t => t.Categoria)
                        .Select(g => new { Categoria = g.Key, Total = g.Sum(t => t.Valor) })
                        .OrderByDescending(g => g.Total)
                        .ToList();

                    // Pede para a IA formatar a resposta de forma humanizada
                    string promptResposta = $@"
                        O usuário perguntou: '{requisicao.Texto}'
                        Os dados encontrados no banco foram:
                        - Total: R${totalConsulta:F2}
                        - Quantidade de transações: {quantidadeConsulta}
                        - Por categoria: {JsonSerializer.Serialize(porCategoriaConsulta)}
                        - Período: de {dataInicio:dd/MM/yyyy} até {dataFim:dd/MM/yyyy}

                        Responda de forma humanizada, amigável e resumida em português.
                        Não use markdown. Seja direto. Máximo 2 linhas.";

                    var corpoResposta = new
                    {
                        contents = new[]
                        {
                            new { parts = new[] { new { text = promptResposta } } }
                        }
                    };

                    var respostaFormatada = await _httpClient.PostAsync(
                        urlGoogle,
                        new StringContent(JsonSerializer.Serialize(corpoResposta), Encoding.UTF8, "application/json")
                    );

                    var jsonRespostaFormatada = await respostaFormatada.Content.ReadAsStringAsync();

                    using var docFormatado = JsonDocument.Parse(jsonRespostaFormatada);

                    var respostaHumanizada = docFormatado.RootElement
                        .GetProperty("candidates")[0]
                        .GetProperty("content")
                        .GetProperty("parts")[0]
                        .GetProperty("text").GetString();

                    return Ok(new RespostaChat
                    {
                        Tipo = "pergunta",
                        Mensagem = respostaHumanizada!.Trim(),
                        TransacaoPendente = null
                    });
                }

                    // USUÁRIO CANCELOU
                case "cancelado":
                    return Ok(new RespostaChat
                    {
                        Tipo = "cancelado",
                        Mensagem = "Ok, a transação não foi registrada.",
                        TransacaoPendente = null
                    });
                case "confirmado":
                    return Ok(new RespostaChat
                    {
                        Tipo = "confirmado",
                        Mensagem = "",
                        TransacaoPendente = null
                    });

                // CLASSIFICAÇÃO DESCONHECIDA
                default:
                    return Ok(new RespostaChat
                    {
                        Tipo = "erro",
                        Mensagem = "Não consegui entender. Tente enviar assim: 'Gastei R$50 em alimentação hoje'",
                        TransacaoPendente = null
                    });
            } 

        }

        // ═══════════════════════════════════════
        // ENDPOINT 2 — CONFIRMAR
        // ═══════════════════════════════════════
        [HttpPost("confirmar")]
        public async Task<IActionResult> Confirmar([FromBody] Transacao transacao)
        {
            // O Flutter manda a transacaoPendente que guardou na memória
            // quando a IA retornou "confirmacao_pendente"

            if (transacao == null)
                return BadRequest("Nenhuma transação recebida.");

            // Normaliza o Tipo por segurança (mesma lógica dos outros endpoints)
            var tipoNormalizado = transacao.Tipo?.Trim().ToLower();

            if (tipoNormalizado != "receita" && tipoNormalizado != "despesa")
                return BadRequest($"Tipo inválido: '{transacao.Tipo}'.");

            transacao.Tipo = char.ToUpper(tipoNormalizado[0]) + tipoNormalizado[1..];

            // Salva no banco
            _context.Transacoes.Add(transacao);
            await _context.SaveChangesAsync();

            return Ok(new RespostaChat
            {
                Tipo = "salvo",
                Mensagem = $"✅ {transacao.Tipo} de R${transacao.Valor:F2} em {transacao.Categoria} registrada com sucesso!",
                TransacaoPendente = null
            });
        }
    }
}