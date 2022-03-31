using System;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Mods.AS.Traits;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Traits.Render;
using OpenRA.Mods.Common.Widgets;
using OpenRA.Primitives;
using OpenRA.Traits;
using OpenRA.Widgets;

namespace OpenRA.Mods.AS.Widgets
{
	public class ActorIconWidget : Widget
	{
		public readonly int2 IconSize;
		public readonly int2 IconPos;
		public readonly string NoIconImage = "icon";
		public readonly string NoIconSequence = "xxicon";
		public readonly string NoIconPalette = "chrome";
		public readonly string DefaultIconImage = "icon";
		public readonly string DefaultIconSequence = "xxicon";
		public readonly string DefaultIconPalette = "chrome";

		public readonly string TooltipTemplate = "ARMY_TOOLTIP";
		public readonly string TooltipContainer;


		public readonly string ClickSound = ChromeMetrics.Get<string>("ClickSound");
		public readonly string ClickDisabledSound = ChromeMetrics.Get<string>("ClickDisabledSound");

		public ArmyUnit TooltipUnit { get; private set; }
		public Func<ArmyUnit> GetTooltipUnit;

		readonly ModData modData;
		readonly WorldRenderer worldRenderer;
		Animation icon;
		ActorStatValues stats;
		Lazy<TooltipContainerWidget> tooltipContainer;

		Player player;
		World world;
		float2 iconOffset;

		public Func<Actor> GetActor;
		Actor actor;

		string currentPalette;
		bool currentPaletteIsPlayerPalette;
		ISelection selection;

		[ObjectCreator.UseCtor]
		public ActorIconWidget(ModData modData, World world, WorldRenderer worldRenderer)
		{
			this.modData = modData;
			this.world = world;
			this.worldRenderer = worldRenderer;
			selection = world.WorldActor.Trait<ISelection>();

			iconOffset = 0.5f * IconSize.ToFloat2() + IconPos;

			currentPalette = NoIconPalette;
			currentPaletteIsPlayerPalette = false;
			icon = new Animation(worldRenderer.World, NoIconImage);
			icon.Play(NoIconSequence);

			GetTooltipUnit = () => TooltipUnit;
			tooltipContainer = Exts.Lazy(() =>
				Ui.Root.Get<TooltipContainerWidget>(TooltipContainer));
		}

		protected ActorIconWidget(ActorIconWidget other)
			: base(other)
		{
			this.modData = other.modData;
			this.world = other.world;
			this.worldRenderer = other.worldRenderer;
			selection = other.selection;

			IconSize = other.IconSize;
			IconPos = other.IconPos;
			NoIconImage = other.NoIconImage;
			NoIconSequence = other.NoIconSequence;
			NoIconPalette = other.NoIconPalette;
			DefaultIconImage = other.DefaultIconImage;
			DefaultIconSequence = other.DefaultIconSequence;
			DefaultIconPalette = other.DefaultIconPalette;

			icon = other.icon;

			TooltipUnit = other.TooltipUnit;
			GetTooltipUnit = () => TooltipUnit;

			TooltipTemplate = other.TooltipTemplate;
			TooltipContainer = other.TooltipContainer;

			tooltipContainer = Exts.Lazy(() =>
				Ui.Root.Get<TooltipContainerWidget>(TooltipContainer));
		}

		public override void Initialize(WidgetArgs args)
		{
			base.Initialize(args);
		}

		public void RefreshIcons()
		{
			actor = GetActor();
			if (actor == null || !actor.IsInWorld || actor.IsDead || actor.Disposed)
			{
				currentPalette = NoIconPalette;
				currentPaletteIsPlayerPalette = false;
				icon = new Animation(worldRenderer.World, NoIconImage);
				icon.Play(NoIconSequence);
				player = null;
				TooltipUnit = null;
				stats = null;
				return;
			}

			player = actor.Owner;
			var rs = actor.Trait<RenderSprites>();
			if (rs == null)
			{
				currentPalette = DefaultIconPalette;
				currentPaletteIsPlayerPalette = false;
				icon = new Animation(worldRenderer.World, DefaultIconImage);
				icon.Play(DefaultIconSequence);
				return;
			}

			stats = actor.TraitOrDefault<ActorStatValues>();
			if (!string.IsNullOrEmpty(stats.Icon))
			{
				currentPaletteIsPlayerPalette = stats.IconPaletteIsPlayerPalette;
				currentPalette = currentPaletteIsPlayerPalette ? stats.IconPalette + player.InternalName : stats.IconPalette;
				icon = new Animation(worldRenderer.World, rs.GetImage(actor));
				icon.Play(stats.Icon);
			}
			else
			{
				currentPalette = DefaultIconPalette;
				currentPaletteIsPlayerPalette = false;
				icon = new Animation(worldRenderer.World, DefaultIconImage);
				icon.Play(DefaultIconSequence);
			}

			if (stats.TooltipActor.HasTraitInfo<BuildableInfo>())
				TooltipUnit = new ArmyUnit(stats.TooltipActor, player);
			else
				TooltipUnit = null;
		}

		public override void Draw()
		{
			Game.Renderer.EnableAntialiasingFilter();

			if (icon.Image != null)
				WidgetUtils.DrawSpriteCentered(icon.Image, worldRenderer.Palette(currentPalette), IconPos + (0.5f * IconSize.ToFloat2()) + RenderBounds.Location);

			if (stats != null)
			{
				foreach (var iconOverlay in stats.IconOverlays.Where(io => !io.IsTraitDisabled))
				{
					var palette = iconOverlay.Info.IsPlayerPalette ? iconOverlay.Info.Palette + player.InternalName : iconOverlay.Info.Palette;
					WidgetUtils.DrawSpriteCentered(iconOverlay.Sprite, worldRenderer.Palette(palette), IconPos + (0.5f * IconSize.ToFloat2()) + RenderBounds.Location + iconOverlay.GetOffset(IconSize));
				}
			}

			Game.Renderer.DisableAntialiasingFilter();
		}

		public override void Tick()
		{
			RefreshIcons();
		}

		public override void MouseEntered()
		{
			if (TooltipContainer != null)
			{
				tooltipContainer.Value.SetTooltip(TooltipTemplate,
					new WidgetArgs() { { "player", world.LocalPlayer }, { "getTooltipUnit", GetTooltipUnit }, { "world", world } });
			}
		}

		public override void MouseExited()
		{
			if (TooltipContainer != null)
				tooltipContainer.Value.RemoveTooltip();
		}

		public override bool HandleMouseInput(MouseInput mi)
		{
			if (mi.Event == MouseInputEvent.Down)
			{
				if (actor == null)
				{
					Game.Sound.PlayNotification(world.Map.Rules, null, "Sounds", ClickDisabledSound, null);
				}
				else
				{
					if (mi.Button == MouseButton.Left)
					{
						worldRenderer.Viewport.Center(actor.CenterPosition);
						Game.Sound.PlayNotification(world.Map.Rules, null, "Sounds", ClickSound, null);
					}
					else if (mi.Button == MouseButton.Right)
					{
						selection.Remove(actor);
						Game.Sound.PlayNotification(world.Map.Rules, null, "Sounds", ClickSound, null);
					}
				}
			}

			return true;
		}

		public override Widget Clone() { return new ActorIconWidget(this); }
	}
}
