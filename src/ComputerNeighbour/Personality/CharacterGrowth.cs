using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ComputerNeighbour.Personality;

public sealed record CharacterGrowthUpdate(string Action,string Kind,string Name,string Expression,string Evidence);
public sealed record CharacterTrait(string Kind,string Name,string Expression,int Strength,string LastSignal)
{
    [JsonIgnore]
    public bool Established=>Strength>=2;
}

internal static class CharacterGrowth
{
    private const int Capacity=8;

    public static IReadOnlyList<CharacterTrait> Apply(IReadOnlyList<CharacterTrait> traits,CharacterGrowthUpdate? update,string? userText,string? reply)
    {
        if(update is null||string.IsNullOrWhiteSpace(userText)||update.Kind is not ("habit" or "interest")||
            update.Action is not ("explore" or "reinforce" or "reconsider")||
            !Clean(update.Name,3,64)||!Clean(update.Expression,8,160)||!Clean(update.Evidence,3,120))return traits;
        var evidence=update.Evidence.Trim();
        var userSignal=userText.Contains(evidence,StringComparison.OrdinalIgnoreCase);
        var expressed=update.Action=="explore"&&reply?.Contains(evidence,StringComparison.OrdinalIgnoreCase)==true;
        if(!userSignal&&!expressed)return traits;

        var name=Regex.Replace(update.Name.Trim().Replace('_',' '),@"\s+"," ");
        var expression=update.Expression.Trim();
        if(expression.StartsWith("User ",StringComparison.OrdinalIgnoreCase)||expression.StartsWith("The user ",StringComparison.OrdinalIgnoreCase))return traits;
        var signal=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Regex.Replace(userText.Trim().ToUpperInvariant(),@"\s+"," "))));
        var next=traits.ToList();
        var index=next.FindIndex(trait=>trait.Kind==update.Kind&&trait.Name.Equals(name,StringComparison.OrdinalIgnoreCase));
        if(index<0)
        {
            if(update.Action!="explore")return traits;
            if(next.Count>=Capacity)
            {
                var oldestExperiment=next.FindIndex(trait=>!trait.Established);
                if(oldestExperiment<0)return traits;
                next.RemoveAt(oldestExperiment);
            }
            next.Add(new(update.Kind,name,expression,1,signal));
        }
        else
        {
            var current=next[index];
            if(signal==current.LastSignal||update.Action=="explore"&&!userSignal)return traits;
            next.RemoveAt(index);
            if(update.Action=="reinforce"||update.Action=="explore")
                next.Add(current with {Expression=expression,Strength=Math.Min(4,current.Strength+1),LastSignal=signal});
            else if(current.Strength>1)next.Add(current with {Strength=current.Strength-1,LastSignal=signal});
        }
        return next.AsReadOnly();
    }

    public static bool IsValid(IReadOnlyList<CharacterTrait>? traits)=>traits is {Count:<=Capacity}
        && traits.All(t=>t is not null&&t.Kind is ("habit" or "interest")&&Clean(t.Name,3,64)&&Clean(t.Expression,8,160)
            &&t.Strength is >=1 and <=4&&t.LastSignal is {Length:64}&&t.LastSignal.All(Uri.IsHexDigit))
        && traits.DistinctBy(t=>t.Kind+" "+t.Name,StringComparer.OrdinalIgnoreCase).Count()==traits.Count;

    public static string Describe(IReadOnlyList<CharacterTrait> traits)
    {
        if(traits.Count==0)return "No habits or interests have settled yet.";
        return string.Join("\n",traits.OrderByDescending(t=>t.Strength).Select(t=>
            $"- {(t.Established?"Established":"Exploring")} {t.Kind} · {t.Name}. {t.Expression}"));
    }

    private static bool Clean(string? text,int min,int max)=>text is not null&&text.Trim().Length>=min&&text.Length<=max
        &&!text.Any(char.IsControl)&&text.Any(char.IsLetterOrDigit);
}
