namespace Game.Combat {
  public class PlayerController {
    public float Speed = 6.2f;
    public int Health { get; set; } = 100;
    public void Jump() {}
    public void DoubleJump() {}
  }
  public class Enemy { public string Name { get; set; } public int Damage { get; set; }
    public void Attack() {} }
}
namespace Game.Combat { public class BossRush { public int Waves = 12; public string RewardTier { get; set; } } }
