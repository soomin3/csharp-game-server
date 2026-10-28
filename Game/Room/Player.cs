using Game.Network;

namespace Game.Room
{
    public class Player
    {
        public Player(IGameSession session)
        {
            Session = session;
        }

        public IGameSession Session { get; }
        public float X { get; set; }
        public float Y { get; set; }
        public bool Moved { get; set; }
    }
}