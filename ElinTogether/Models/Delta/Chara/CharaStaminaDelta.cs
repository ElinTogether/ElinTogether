using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class CharaStaminaDelta : ElinDelta
{
    [Key(0)]
    public required RemoteCard Chara { get; init; }

    [Key(1)]
    public required int Stamina { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (Chara.Find() is not Chara { IsPC: false } chara) {
            return;
        }

        chara.stamina.value = Stamina;
    }
}